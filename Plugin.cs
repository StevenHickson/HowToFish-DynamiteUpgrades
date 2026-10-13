using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DynamiteUpgrades
{
    [BepInPlugin(Guid, "Dynamite Upgrades", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "justresearch.dynamiteupgrades";

        internal static ManualLogSource Log;

        // Index 0 is plain dynamite; each later entry is one upgrade step.
        internal static Tier[] Tiers;
        internal static int MaxTier => Tiers.Length - 1;

        void Awake()
        {
            Log = Logger;
            Tiers = new[]
            {
                Tier.Normal,
                new Tier(Config, "Strong", island: 3, cost: 30, damage: 1.6f, radius: 1.3f, force: 1.3f, fish: 1.5f, shake: 1.3f,
                    color: new Color(1f, 0.55f, 0f)),
                new Tier(Config, "Super", island: 4, cost: 50, damage: 3f, radius: 1.75f, force: 1.8f, fish: 2.5f, shake: 1.8f,
                    color: new Color(0.2f, 0.45f, 1f)),
                new Tier(Config, "Mega", island: 5, cost: 80, damage: 5f, radius: 2.2f, force: 2.4f, fish: 4f, shake: 2.4f,
                    color: new Color(0.7f, 0.2f, 1f)),
            };

            new Harmony(Guid).PatchAll();
            Log.LogInfo("Dynamite Upgrades loaded");
        }
    }

    internal class Tier
    {
        public static readonly Tier Normal = new Tier();

        public string Name = "";
        public ConfigEntry<int> Island, Cost;
        public ConfigEntry<float> Damage, Radius, Force, Fish, Shake;
        public ConfigEntry<Color> Color;

        Tier() { }

        public Tier(ConfigFile cfg, string name, int island, int cost, float damage, float radius, float force, float fish,
            float shake, Color color)
        {
            Name = name;
            string s = name + " Dynamite";
            Island = cfg.Bind(s, "AvailableFromIsland", island, "First island (1-5) whose shop counter offers this upgrade, like the gun upgrades");
            Cost = cfg.Bind(s, "Cost", cost, "Price of this upgrade at the bullet upgrade counter (a stick of dynamite costs $25)");
            Damage = cfg.Bind(s, "DamageMultiplier", damage, "Explosion damage multiplier");
            Radius = cfg.Bind(s, "RadiusMultiplier", radius, "Damage and push radius multiplier");
            Force = cfg.Bind(s, "ForceMultiplier", force, "How hard items, players and boats get thrown");
            Fish = cfg.Bind(s, "FishMultiplier", fish, "How many fish an underwater blast brings up");
            Shake = cfg.Bind(s, "ScreenShakeMultiplier", shake, "Screen shake multiplier (cosmetic)");
            Color = cfg.Bind(s, "Color", color, "Color of the stick. Its hue replaces the red of normal dynamite; white and grey parts stay as they are");
        }
    }

    /// <summary>
    /// The tier lives in the item's synced "random weight", which the game only uses for fish.
    /// For dynamite it is always 1.0, so 1.0001 = Strong, 1.0002 = Super, 1.0003 = Mega. The host sets it and FishNet
    /// syncs it to every client for free; the sell price is floored to an int so it doesn't change.
    /// </summary>
    internal static class DynamiteTier
    {
        const float Step = 0.0001f;

        public static int Get(Item item) => item ? Decode(item._syncedRandomWeight.Value) : 0;

        public static int Decode(float w)
        {
            int t = Mathf.RoundToInt((w - 1f) / Step);
            if (t <= 0 || t > Plugin.MaxTier || Mathf.Abs(w - (1f + t * Step)) > Step * 0.25f) return 0;
            return t;
        }

        /// <summary>1-based number of the main island everyone is on (0 before the world has loaded).</summary>
        public static int CurrentIsland => OnlineIslandManager.Instance ? OnlineIslandManager.CurIsland + 1 : 0;

        public static bool IsUnlocked(int tier) => CurrentIsland >= Plugin.Tiers[tier].Island.Value;

        public static float Encode(int tier) => 1f + tier * Step;

        public static void ServerSet(Item item, int tier) => item._syncedRandomWeight.Value = Encode(tier);

        public static (int cost, Currency currency) CostFor(Item item, int tier)
        {
            var t = Plugin.Tiers[tier];
            return (Mathf.Max(0, t.Cost.Value), item._currency);
        }

        public static bool TryGetHeld(Player player, out Explosive dynamite)
        {
            dynamite = null;
            if (!player) return false;
            var held = player.Holding ? player.Holding.HeldItem : null;
            if (!held || held.SyncedHolder != player) return false;
            dynamite = held as Explosive;
            return dynamite;
        }

        // On the host we can't rely on a remote player's local "held item", so find the stick they are synced as holding.
        public static Explosive FindHeldOnServer(Player player)
        {
            foreach (var e in UnityEngine.Object.FindObjectsByType<Explosive>())
                if (e.SyncedHolder == player) return e;
            return null;
        }
    }

    internal static class Scaling
    {
        static readonly MethodInfo Clone = AccessTools.Method(typeof(object), "MemberwiseClone");
        static readonly ConditionalWeakTable<ExplosionInfo, object> Scaled = new ConditionalWeakTable<ExplosionInfo, object>();

        public static bool IsScaled(ExplosionInfo info) => Scaled.TryGetValue(info, out _);

        public static ExplosionInfo Scale(ExplosionInfo src, int tier)
        {
            var t = Plugin.Tiers[tier];
            var c = (ExplosionInfo)Clone.Invoke(src, null);
            c._damage = Mathf.RoundToInt(c._damage * t.Damage.Value);
            c.damageRadius *= t.Radius.Value;
            c._forceRadius *= t.Radius.Value;
            c._itemForce *= t.Force.Value;
            c._boatForce *= t.Force.Value;
            c._playerForce *= t.Force.Value;
            c._underwaterFishMinMax = new Vector2Int(
                Mathf.RoundToInt(c._underwaterFishMinMax.x * t.Fish.Value),
                Mathf.RoundToInt(c._underwaterFishMinMax.y * t.Fish.Value));
            c._screenShakeAmount *= t.Shake.Value;
            Scaled.Add(c, null);
            return c;
        }
    }

    // ---------------------------------------------------------------- host side: the actual explosion

    [HarmonyPatch(typeof(ExplosionManager), nameof(ExplosionManager.ServerExplode))]
    static class ServerExplodePatch
    {
        static void Prefix(Item item, ref ExplosionInfo info, out ExplosionInfo __state)
        {
            __state = null;
            if (!(item is Explosive) || info == null || Scaling.IsScaled(info)) return;
            int tier = DynamiteTier.Get(item);
            if (tier == 0) return;
            __state = info;
            info = Scaling.Scale(info, tier);
        }

        // Keep the original's "already exploded" flag in step, so the game's own once-only checks still work.
        static void Postfix(ref ExplosionInfo info, ExplosionInfo __state)
        {
            if (__state != null && info != null) __state.HasExploded = info.HasExploded;
        }
    }

    // ---------------------------------------------------------------- every client: bigger screen shake

    [HarmonyPatch]
    static class ObserverExplodePatch
    {
        internal static Item Current;

        static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(ExplosionManager)).First(m => m.Name.StartsWith("RpcLogic___ObserverExplode"));

        static void Prefix(Item __0) => Current = __0;
        static void Finalizer() => Current = null;
    }

    [HarmonyPatch(typeof(ExplosionManager), nameof(ExplosionManager.ExplodeEffects))]
    static class ExplodeEffectsPatch
    {
        static void Prefix(ref ExplosionInfo info)
        {
            int tier = DynamiteTier.Get(ObserverExplodePatch.Current);
            if (tier > 0 && info != null && !Scaling.IsScaled(info)) info = Scaling.Scale(info, tier);
        }
    }

    // ---------------------------------------------------------------- shop: the ammo counter also upgrades dynamite

    [HarmonyPatch]
    static class BaseCalls
    {
        // Non-virtual calls to the base-class methods (calling them normally would loop back into our patches).
        [HarmonyReversePatch, HarmonyPatch(typeof(Purchasable), nameof(Purchasable.Hover))]
        public static void PurchasableHover(Purchasable instance) => throw new NotImplementedException("stub");

        [HarmonyReversePatch, HarmonyPatch(typeof(Interactable), nameof(Interactable.Interact))]
        public static void InteractableInteract(Interactable instance, Player player) => throw new NotImplementedException("stub");
    }

    [HarmonyPatch(typeof(BulletPurchasable), nameof(BulletPurchasable.Hover))]
    static class CounterHoverPatch
    {
        static bool Prefix(BulletPurchasable __instance)
        {
            var player = Player.LocalPlayer;
            if (__instance._forArrows || !DynamiteTier.TryGetHeld(player, out var dynamite)) return true;

            int tier = DynamiteTier.Get(dynamite);
            string status = "", price = "";
            __instance._upgrade = "Upgrade Dynamite";

            if (tier < Plugin.MaxTier && !DynamiteTier.IsUnlocked(tier + 1))
            {
                __instance._customCanBuy = false;
                status = "(Max for this island)";
                __instance._fullDescription = "Max for this island";
            }
            else if (tier < Plugin.MaxTier)
            {
                var next = Plugin.Tiers[tier + 1];
                var (cost, currency) = DynamiteTier.CostFor(dynamite, tier + 1);
                __instance._customCost = cost;
                __instance._customCurrency = currency;
                __instance._customCanBuy = true;
                price = $"\n{(currency == Currency.Euro ? '€' : '$')}{cost}";
                __instance._fullDescription =
                    $"{(tier == 0 ? "Dynamite" : Plugin.Tiers[tier].Name + " Dynamite")} -> {next.Name} Dynamite\n" +
                    $"Damage x{next.Damage.Value:0.##}   Radius x{next.Radius.Value:0.##}\n" +
                    $"Fish from underwater blasts x{next.Fish.Value:0.##}";
            }
            else
            {
                __instance._customCanBuy = false;
                status = "(Max power)";
                __instance._fullDescription = "This dynamite can't get any stronger.";
            }

            string hover = __instance._isFree
                ? __instance._upgrade + "\n" + SpriteManager.GetPickUpInput()
                : __instance._upgrade + " " + status + price + "\n" + SpriteManager.GetPickUpInput();
            if (hover != __instance._hoverString)
            {
                __instance._isHovering = false; // makes the game refresh the description panel
                __instance._hoverString = hover;
            }
            BaseCalls.PurchasableHover(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(BulletPurchasable), nameof(BulletPurchasable.Interact))]
    static class CounterInteractPatch
    {
        // Sent to the host through the existing "buy bait" RPC. A host without the mod ignores it
        // (the bait index is out of range), so nothing breaks and no money is taken.
        internal const byte UpgradeDynamiteCode = 0xD7;

        static bool Prefix(BulletPurchasable __instance, Player player)
        {
            if (__instance._forArrows || !DynamiteTier.TryGetHeld(player, out var dynamite)) return true;
            if (!Server.Instance) return false;

            BaseCalls.InteractableInteract(__instance, player);

            int tier = DynamiteTier.Get(dynamite);
            if (tier >= Plugin.MaxTier || !DynamiteTier.IsUnlocked(tier + 1)) { __instance.CantBuyEffects(); return false; }

            var (cost, currency) = DynamiteTier.CostFor(dynamite, tier + 1);
            if (!MoneyManager.CanAfford(cost, currency)) { __instance.CantBuyEffects(); return false; }

            Server.Instance.BuyBait(player, UpgradeDynamiteCode, cost);
            return false;
        }
    }

    [HarmonyPatch]
    static class ServerBuyBaitPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(Server)).First(m => m.Name.StartsWith("RpcLogic___BuyBait"));

        static bool Prefix(Player __0, byte __1)
        {
            if (__1 != CounterInteractPatch.UpgradeDynamiteCode) return true;

            var player = __0;
            if (!player || player.IsDeinitializing) return false;
            var dynamite = DynamiteTier.FindHeldOnServer(player);
            if (!dynamite) return false;

            int tier = DynamiteTier.Get(dynamite);
            if (tier >= Plugin.MaxTier || !DynamiteTier.IsUnlocked(tier + 1)) return false;

            // The host checks the price with its own config, whatever the client claimed.
            var (cost, currency) = DynamiteTier.CostFor(dynamite, tier + 1);
            if (!MoneyManager.CanAfford(cost, currency)) return false;
            MoneyManager.RemoveMoney(cost, player, currency);
            DynamiteTier.ServerSet(dynamite, tier + 1);
            Plugin.Log.LogInfo($"{player.SteamName} upgraded dynamite to {Plugin.Tiers[tier + 1].Name} for {cost}");
            return false;
        }
    }

    // ---------------------------------------------------------------- saving: keep the tier across quit and reload

    // SavedItem already has a Weight field, but the game only fills it in for fish.
    // Every save (inventory, held item, items on the ground) goes through ItemToSavedItem.
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.ItemToSavedItem))]
    static class SaveTierPatch
    {
        static void Postfix(Item item, SavedItem __result)
        {
            if (__result == null || !(item is Explosive)) return;
            int tier = DynamiteTier.Get(item);
            if (tier > 0) __result.Weight = DynamiteTier.Encode(tier);
        }
    }

    // Every load goes through Item.LoadFromSave, which runs on the host before the item is handed to players.
    [HarmonyPatch(typeof(Item), nameof(Item.LoadFromSave))]
    static class LoadTierPatch
    {
        static void Postfix(Item __instance, SavedItem savedItem)
        {
            if (savedItem == null || !(__instance is Explosive)) return;
            int tier = DynamiteTier.Decode(savedItem.Weight);
            if (tier > 0) DynamiteTier.ServerSet(__instance, tier);
        }
    }

    // ---------------------------------------------------------------- looks: name and color

    [HarmonyPatch(typeof(Item), nameof(Item.GetName))]
    static class ItemNamePatch
    {
        static void Postfix(Item __instance, ref string __result)
        {
            if (!(__instance is Explosive)) return;
            int tier = DynamiteTier.Get(__instance);
            if (tier > 0) __result = Plugin.Tiers[tier].Name + " " + __result;
        }
    }

    /// <summary>
    /// The game's "DefaultShader" takes its colors from a palette texture (skins work by shifting where the
    /// palette is sampled), so there is no color property to tint. Instead each upgraded stick gets its own
    /// copy of that texture with the hue swapped to the tier color.
    /// </summary>
    internal static class Recolor
    {
        static readonly Dictionary<(Texture, int), Texture2D> Cache = new Dictionary<(Texture, int), Texture2D>();
        static readonly ConditionalWeakTable<Material, Dictionary<string, Texture>> Originals =
            new ConditionalWeakTable<Material, Dictionary<string, Texture>>();
        static bool _loggedShader;

        /// <summary>Recolors the renderer's own material instance for this tier; tier 0 puts the original textures back.</summary>
        public static void Apply(Renderer renderer, int tier)
        {
            if (!renderer) return;
            foreach (var mat in renderer.materials)
            {
                if (!mat) continue;
                var originals = Originals.GetValue(mat, Capture);
                foreach (var kv in originals)
                    mat.SetTexture(kv.Key, tier == 0 ? kv.Value : Tinted(kv.Value, tier));
            }
        }

        static Dictionary<string, Texture> Capture(Material mat)
        {
            if (!_loggedShader)
            {
                _loggedShader = true;
                var sh = mat.shader;
                var props = Enumerable.Range(0, sh.GetPropertyCount()).Select(i => $"{sh.GetPropertyName(i)}:{sh.GetPropertyType(i)}");
                Plugin.Log.LogInfo($"Dynamite shader '{sh.name}' properties: {string.Join(", ", props)}");
            }

            var result = new Dictionary<string, Texture>();
            foreach (var name in mat.GetTexturePropertyNames())
            {
                var tex = mat.GetTexture(name);
                string lower = name.ToLowerInvariant();
                if (!(tex is Texture2D) || lower.Contains("normal") || lower.Contains("bump")) continue;
                result[name] = tex;
                Plugin.Log.LogInfo($"Recoloring texture {name} = '{tex.name}' ({tex.width}x{tex.height})");
            }
            return result;
        }

        static Texture2D Tinted(Texture src, int tier)
        {
            if (Cache.TryGetValue((src, tier), out var cached) && cached) return cached;

            // The source texture usually isn't CPU-readable, so copy it through a RenderTexture first.
            int w = src.width, h = src.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            bool mips = src.mipmapCount > 1;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mips, false)
            {
                name = src.name + "_" + Plugin.Tiers[tier].Name,
                filterMode = src.filterMode,
                wrapMode = src.wrapMode,
                anisoLevel = src.anisoLevel,
            };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            Color.RGBToHSV(Plugin.Tiers[tier].Color.Value, out float th, out float ts, out _);
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                Color.RGBToHSV(c, out _, out float s, out float v);
                if (s < 0.2f) continue; // whites, greys and blacks keep their look
                Color n = Color.HSVToRGB(th, Mathf.Lerp(s, ts, 0.5f), v);
                n.a = c.a;
                px[i] = n;
            }
            tex.SetPixels32(px);
            tex.Apply(mips, false);
            Cache[(src, tier)] = tex;
            return tex;
        }
    }

    // Sticks in the world and in hand: re-apply whenever a stick's tier changes (it is synced from the host).
    [HarmonyPatch(typeof(Explosive), nameof(Explosive.Update))]
    static class TintPatch
    {
        class Seen { public int Tier; }
        static readonly ConditionalWeakTable<Explosive, Seen> Applied = new ConditionalWeakTable<Explosive, Seen>();

        static void Postfix(Explosive __instance)
        {
            int tier = DynamiteTier.Get(__instance);
            var seen = Applied.GetOrCreateValue(__instance);
            if (seen.Tier == tier) return;
            seen.Tier = tier;

            foreach (var r in __instance._renderers) Recolor.Apply(r, tier);
            InventoryTintPatch.RefreshSlotsShowing(__instance);
        }
    }

    // Inventory icons: each slot has one renderer that gets reused for whatever item is in it.
    [HarmonyPatch(typeof(InventorySlot), nameof(InventorySlot.ApplySkin))]
    static class InventoryTintPatch
    {
        static readonly HashSet<InventorySlot> Tinted = new HashSet<InventorySlot>();

        static void Postfix(InventorySlot __instance, Item item) => Refresh(__instance, item);

        static void Refresh(InventorySlot slot, Item item)
        {
            if (!slot || !slot._renderer) return;
            int tier = item is Explosive ? DynamiteTier.Get(item) : 0;
            if (tier == 0 && !Tinted.Contains(slot)) return; // never touched, nothing to undo
            Recolor.Apply(slot._renderer, tier);
            if (tier > 0) Tinted.Add(slot); else Tinted.Remove(slot);
        }

        public static void RefreshSlotsShowing(Item item)
        {
            var player = Player.LocalPlayer;
            if (!player || !player.Inventory || player.Inventory._itemSlots == null) return;
            foreach (var slot in player.Inventory._itemSlots)
                if (slot && slot.Item == item) Refresh(slot, item);
        }
    }
}
