using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Familiars — miniature flying creatures bound to the equipped HELMET, hovering
// at the player's shoulder, with a matching name suffix. Each also grants
// feather fall (shared with the Feather Cape) and a small flat attack bonus
// (SE_FamiliarBond) while equipped — light utility, not a stat-stacking build:
// the bonus is 1-4% of endgame weapon damage, so it stays inside the "never
// helps you win a fight" balance rule.
//
// The Ghost keeps its original particle-child + point-light build (the look
// that started this category); the other seven clone the creature prefab's
// WHOLE VISUAL inside an inactive holder (so Awake never runs on its
// AI/network/physics), then strip everything but renderers/particles/animator/
// LODs/lights, in dependency order (a component another still
// [RequireComponent]s is skipped, not force-removed) — nothing real ever
// spawns (no ZNetView/ZDO, no Character, no colliders).
//
// Data model (two layers):
//   * SOURCE OF TRUTH — the aura is stamped on the equipped item instance via
//     ItemData.m_customData["vc_armor_vfx"]. That persists with the item across
//     relogs and drives the rename (GetTooltip postfix).
//   * BROADCAST — the local player mirrors "which aura is on each equipped slot"
//     onto their own Player ZDO (keys vc_vfx_head/chest/legs). ZDO replicates to
//     every client, so other players running this plugin see the aura too. A
//     client-side manager reads every player's ZDO and attaches/detaches the
//     particles on the matching body part.
//
// PROTOTYPE: resolution is runtime + logged under [Valcoin][ArmorVfx]. If a
// prefab or child can't be found, the purchase + rename still succeed and the
// visual degrades — never crashes.
public static class ArmorVfx
{
    public const string ItemKey = "vc_armor_vfx";

    public sealed class Aura
    {
        public string Id, Slot, Suffix, Display;
        public string ParentPrefab;    // creature/item prefab (child-clone or whole-creature source)
        public string[] ChildHints;    // lowercase name fragments to find a particle child
        public bool   AnyChildOk;      // hints miss => take the first particle child anyway
        public bool   WholeCreature;   // clone the creature's whole VISUAL (renderers/particles/animator)
        public string Fallback;        // standalone effect prefab if resolution fails
        public float  Scale = 1f;      // shrink to mini-pet size
        public float  Raise;           // extra hover height above CompanionOffset
        public bool   TameParticles;   // force particle systems to respect the tiny scale
        public string[] StripChildHints; // destroy child vfx whose name matches (e.g. Gjall drips)
        public bool   HasLight;        // add a real point light (guaranteed-visible glow)
        public Color  LightColor;
        // Flat damage added to the owner's attacks while this familiar is
        // equipped (via SE_FamiliarBond). Small on purpose — flavor, not power.
        public float  Slash, Pierce, Blunt, Fire, Frost, Spirit;
    }

    // Where every familiar hovers: at the player's RIGHT side, head height.
    public static readonly Vector3 CompanionOffset = new Vector3(0.45f, 1.55f, 0f);

    // "Familiars" — miniature flying creatures hovering at the shoulder, bound
    // to the equipped helmet. All head-slot. Ghost keeps its particle-child +
    // glow build (the look that started this); the rest clone the creature's
    // whole visual, stripped of AI/network/physics inside an inactive holder so
    // nothing real ever spawns. Priced by progression tier.
    public static readonly Dictionary<string, Aura> Registry = new Dictionary<string, Aura>
    {
        ["bat"] = new Aura { Id = "bat", Slot = "head", Display = "Bat",
            Suffix = "of the Bat", ParentPrefab = "Bat", WholeCreature = true, Scale = 0.8f,
            Slash = 2f },
        ["ghostlight"] = new Aura { Id = "ghostlight", Slot = "head", Display = "Ghost",
            Suffix = "of the Ghost", ParentPrefab = "Ghost",
            ChildHints = new[] { "glow", "wisp", "mist", "particle", "body" }, AnyChildOk = true,
            HasLight = true, LightColor = new Color(0.65f, 1f, 0.8f),
            Fallback = "fx_ItemSparkles", Scale = 0.4f,
            Slash = 2f },
        ["deathsquito"] = new Aura { Id = "deathsquito", Slot = "head", Display = "Deathsquito",
            Suffix = "of the Deathsquito", ParentPrefab = "Deathsquito", WholeCreature = true, Scale = 0.6f,
            Pierce = 2f },
        ["hatchling"] = new Aura { Id = "hatchling", Slot = "head", Display = "Drake Hatchling",
            Suffix = "of the Drake", ParentPrefab = "Hatchling", WholeCreature = true, Scale = 0.35f,
            Raise = 0.25f, Frost = 2f },
        ["wraith"] = new Aura { Id = "wraith", Slot = "head", Display = "Wraith",
            Suffix = "of the Wraith", ParentPrefab = "Wraith", WholeCreature = true, Scale = 0.35f,
            Slash = 2f },
        ["volture"] = new Aura { Id = "volture", Slot = "head", Display = "Volture",
            Suffix = "of the Volture", ParentPrefab = "Volture", WholeCreature = true, Scale = 0.3f,
            Raise = 0.3f, Pierce = 3f },
        ["gjall"] = new Aura { Id = "gjall", Slot = "head", Display = "Gjall",
            Suffix = "of the Gjall", ParentPrefab = "Gjall", WholeCreature = true, Scale = 0.08f,
            Raise = 0.35f, TameParticles = true,
            StripChildHints = new[] { "drip", "droplet", "tar", "gland" },
            Blunt = 2f, Fire = 1f },
        ["fallen_valkyrie"] = new Aura { Id = "fallen_valkyrie", Slot = "head", Display = "Fallen Valkyrie",
            Suffix = "of the Valkyrie", ParentPrefab = "FallenValkyrie", WholeCreature = true, Scale = 0.15f,
            Raise = 0.3f, TameParticles = true,
            Spirit = 2f },
    };

    public static readonly string[] Slots = { "head", "chest", "legs" };
    public static bool IsSlot(string s) => Array.IndexOf(Slots, s) >= 0;
    public static string ZKey(string slot) => "vc_vfx_" + slot;

    // The slot an aura is bound to (auras are slot-specific now).
    public static string SlotFor(string auraId)
        => Registry.TryGetValue(auraId ?? "", out var a) ? a.Slot : null;

    // Character.m_nview is protected; the ZNetView is on the same GameObject.
    public static ZNetView NView(Component c) => c != null ? c.GetComponent<ZNetView>() : null;

    // Localize an item name token ("$item_helmet_bronze" -> "Bronze Helmet").
    // Localization is internal to assembly_valheim, so bind it by reflection.
    private static MethodInfo _localize;
    private static PropertyInfo _locInstance;
    private static bool _locResolved;
    public static string LocalizeName(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        try
        {
            if (!_locResolved)
            {
                _locResolved = true;
                var t = AccessTools.TypeByName("Localization");
                if (t != null)
                {
                    _locInstance = AccessTools.Property(t, "instance");
                    _localize = AccessTools.Method(t, "Localize", new[] { typeof(string) });
                }
            }
            var inst = _locInstance?.GetValue(null);
            if (inst != null && _localize != null)
                return (string)_localize.Invoke(inst, new object[] { token });
        }
        catch { }
        return token;
    }

    // ─── feather fall (borrowed from the Feather Cape) ──────────────────────
    // Familiars carry their owner: any equipped familiar helmet grants the
    // game's own SlowFall status effect — the exact StatusEffect asset the
    // Feather Cape equips, so wearing both never stacks (same effect, one icon).
    private static StatusEffect _slowFall;
    private static bool _slowFallResolved;
    public static StatusEffect SlowFallEffect()
    {
        if (_slowFallResolved) return _slowFall;
        if (ObjectDB.instance == null) return null;   // retry once the DB is up
        _slowFallResolved = true;
        try
        {
            var cape = ObjectDB.instance.GetItemPrefab("CapeFeather");
            _slowFall = cape?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_equipStatusEffect;
        }
        catch { }
        Debug.Log($"[Valcoin][ArmorVfx] SlowFall effect -> {(_slowFall != null ? "ok (CapeFeather)" : "NOT FOUND")}");
        return _slowFall;
    }

    // True if an equipped item grants SlowFall on its own (the Feather Cape) —
    // then unequipping the familiar helmet must not strip the effect.
    public static bool WearsSlowFallItem(Humanoid h)
    {
        Reflect();
        var se = SlowFallEffect();
        if (h == null || se == null) return false;
        try
        {
            var shoulder = _fShoulderItem?.GetValue(h) as ItemDrop.ItemData;
            var itemSe = shoulder?.m_shared?.m_equipStatusEffect;
            return itemSe != null && itemSe.NameHash() == se.NameHash();
        }
        catch { return false; }
    }

    // ─── reflection into Humanoid / VisEquipment privates ───────────────────
    private static bool _refl;
    private static FieldInfo _fHelmetItem, _fChestItem, _fLegItem, _fShoulderItem; // Humanoid, ItemData
    private static FieldInfo _fHelmetInst, _fHelmetBone, _fChestInsts, _fLegInsts, _fBodyModel; // VisEquipment

    private static void Reflect()
    {
        if (_refl) return;
        _refl = true;
        _fHelmetItem = AccessTools.Field(typeof(Humanoid), "m_helmetItem");
        _fChestItem  = AccessTools.Field(typeof(Humanoid), "m_chestItem");
        _fLegItem    = AccessTools.Field(typeof(Humanoid), "m_legItem");
        _fShoulderItem = AccessTools.Field(typeof(Humanoid), "m_shoulderItem");
        _fHelmetInst = AccessTools.Field(typeof(VisEquipment), "m_helmetItemInstance");
        _fHelmetBone = AccessTools.Field(typeof(VisEquipment), "m_helmet");
        _fChestInsts = AccessTools.Field(typeof(VisEquipment), "m_chestItemInstances");
        _fLegInsts   = AccessTools.Field(typeof(VisEquipment), "m_legItemInstances");
        _fBodyModel  = AccessTools.Field(typeof(VisEquipment), "m_bodyModel");
    }

    // The equipped ItemData in a slot, or null.
    public static ItemDrop.ItemData EquippedIn(Humanoid h, string slot)
    {
        Reflect();
        if (h == null) return null;
        try
        {
            switch (slot)
            {
                case "head":  return _fHelmetItem?.GetValue(h) as ItemDrop.ItemData;
                case "chest": return _fChestItem?.GetValue(h) as ItemDrop.ItemData;
                case "legs":  return _fLegItem?.GetValue(h) as ItemDrop.ItemData;
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[Valcoin][ArmorVfx] EquippedIn: {ex.Message}"); }
        return null;
    }

    // The aura id stamped on the item equipped in `slot`, or null.
    public static string EquippedAura(Humanoid h, string slot)
    {
        var it = EquippedIn(h, slot);
        if (it?.m_customData == null) return null;
        return it.m_customData.TryGetValue(ItemKey, out var a) && Registry.ContainsKey(a) ? a : null;
    }

    // Called on the buyer's client after a successful spend (via __ARMORVFX__).
    // Stamps the equipped piece in the aura's slot; manager + ZDO do the rest.
    public static bool ApplyToEquipped(string aura, string slot, out string msg)
    {
        if (!Registry.TryGetValue(aura ?? "", out var def)) { msg = "Unknown armor effect."; return false; }
        slot = def.Slot;   // auras are slot-bound; the wire slot is informational

        var p = Player.m_localPlayer;
        var item = EquippedIn(p, slot);
        if (item == null)
        {
            msg = $"You have no {slot} armor equipped — equip a piece, then buy again.";
            return false;
        }

        if (item.m_customData == null) item.m_customData = new Dictionary<string, string>();
        item.m_customData[ItemKey] = aura;
        MirrorLocalToZdo();   // reflect immediately so it shows without waiting for the tick

        string nm = LocalizeName(item.m_shared.m_name);
        msg = $"Applied {def.Display} to your {nm} — now \"{nm} {def.Suffix}\".";
        Debug.Log($"[Valcoin][ArmorVfx] Applied {aura} to {slot} ({nm}).");
        return true;
    }

    // Mirror the local player's equipped-item auras onto their own ZDO so other
    // clients can render them. Only the owner may write its ZDO.
    public static void MirrorLocalToZdo()
    {
        var p = Player.m_localPlayer;
        var nv = NView(p);
        var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
        if (zdo == null || !nv.IsOwner()) return;
        foreach (var slot in Slots)
        {
            var aura = EquippedAura(p, slot) ?? "";
            try { zdo.Set(ZKey(slot), aura); } catch { /* not owner / transient */ }
        }
    }

    // ─── effect-source resolution (cached) ──────────────────────────────────
    private static readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();
    public static GameObject ResolvePrefab(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_prefabCache.TryGetValue(name, out var cached)) return cached;

        GameObject go = null;
        try { if (ZNetScene.instance != null) go = ZNetScene.instance.GetPrefab(name); } catch { }
        if (go == null)
        {
            // Items (DragonTear, trophies) live in ObjectDB, not ZNetScene.
            try { if (ObjectDB.instance != null) go = ObjectDB.instance.GetItemPrefab(name); } catch { }
        }
        if (go == null)
        {
            // Last resort: scan loaded assets. Only finds prefabs whose soft-ref
            // bundle happens to be loaded. Cached, so at most one scan per name.
            try
            {
                foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (g != null && g.name == name) { go = g; break; }
            }
            catch { }
        }
        _prefabCache[name] = go;
        Debug.Log($"[Valcoin][ArmorVfx] Resolve prefab '{name}' -> {(go != null ? "ok" : "NOT FOUND")}");
        return go;
    }

    // The GameObject to clone for an aura: the named particle child of the
    // parent prefab when we can find one, else the standalone fallback.
    private static readonly Dictionary<string, GameObject> _sourceCache = new Dictionary<string, GameObject>();
    public static GameObject ResolveSource(Aura def)
    {
        if (_sourceCache.TryGetValue(def.Id, out var cached)) return cached;

        // Whole-creature familiars clone the creature prefab itself (the visual
        // strip happens at spawn time, inside an inactive holder).
        if (def.WholeCreature)
        {
            var creature = ResolvePrefab(def.ParentPrefab);
            _sourceCache[def.Id] = creature;
            return creature;
        }

        GameObject src = null;
        if (!string.IsNullOrEmpty(def.ParentPrefab) && def.ChildHints != null)
        {
            var parent = ResolvePrefab(def.ParentPrefab);
            if (parent != null)
            {
                GameObject firstPs = null;
                foreach (var tr in parent.GetComponentsInChildren<Transform>(true))
                {
                    if (tr == null || tr.gameObject == parent) continue;
                    bool hasPs = tr.GetComponentInChildren<ParticleSystem>(true) != null;
                    if (!hasPs) continue;
                    if (firstPs == null) firstPs = tr.gameObject;

                    var n = tr.name.ToLowerInvariant();
                    foreach (var hint in def.ChildHints)
                        if (n.Contains(hint)) { src = tr.gameObject; break; }
                    if (src != null) break;
                }
                // Hints missed: for AnyChildOk auras any particle child beats the
                // generic fallback (it's the creature's own effect).
                if (src == null && def.AnyChildOk) src = firstPs;

                if (src != null)
                    Debug.Log($"[Valcoin][ArmorVfx] {def.Id}: child hunt in '{def.ParentPrefab}' -> '{src.name}'");
                else
                {
                    // Dump the child names once so the hints can be tuned from a
                    // live log instead of guessing.
                    var names = new List<string>();
                    foreach (var tr in parent.GetComponentsInChildren<Transform>(true))
                        if (tr != null && tr.gameObject != parent && names.Count < 40)
                            names.Add(tr.name);
                    Debug.Log($"[Valcoin][ArmorVfx] {def.Id}: no child match in '{def.ParentPrefab}'. "
                              + "Children: " + string.Join(", ", names.ToArray()));
                }
            }
        }
        if (src == null) src = ResolvePrefab(def.Fallback);

        _sourceCache[def.Id] = src;
        return src;
    }

    // The transform to parent a slot's aura to on a given player.
    public static Transform AttachPoint(Player p, string slot)
    {
        Reflect();
        if (p == null) return null;
        VisEquipment ve = null;
        try { ve = p.GetComponentInChildren<VisEquipment>(); } catch { }
        if (ve == null) return p.transform;

        try
        {
            switch (slot)
            {
                case "head":
                    var hi = _fHelmetInst?.GetValue(ve) as GameObject;
                    if (hi != null) return hi.transform;
                    var hb = _fHelmetBone?.GetValue(ve) as Transform;
                    return hb != null ? hb : p.transform;
                case "chest":
                    var ct = FirstInstance(_fChestInsts?.GetValue(ve));
                    if (ct != null) return ct;
                    return BodyOr(ve, p);
                case "legs":
                    var lt = FirstInstance(_fLegInsts?.GetValue(ve));
                    if (lt != null) return lt;
                    return BodyOr(ve, p);
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[Valcoin][ArmorVfx] AttachPoint: {ex.Message}"); }
        return p.transform;
    }

    private static Transform BodyOr(VisEquipment ve, Player p)
    {
        var bm = _fBodyModel?.GetValue(ve) as SkinnedMeshRenderer;
        return bm != null ? bm.transform : p.transform;
    }

    private static Transform FirstInstance(object list)
    {
        if (list is System.Collections.IList l)
            foreach (var o in l)
                if (o is GameObject g && g != null) return g.transform;
        return null;
    }

    // Human-readable stat line for an aura ("+2 slash", "+2 blunt, +1 fire").
    public static string StatsText(Aura a)
    {
        var parts = new List<string>();
        if (a.Slash  > 0) parts.Add($"+{a.Slash:0} slash");
        if (a.Pierce > 0) parts.Add($"+{a.Pierce:0} pierce");
        if (a.Blunt  > 0) parts.Add($"+{a.Blunt:0} blunt");
        if (a.Fire   > 0) parts.Add($"+{a.Fire:0} fire");
        if (a.Frost  > 0) parts.Add($"+{a.Frost:0} frost");
        if (a.Spirit > 0) parts.Add($"+{a.Spirit:0} spirit");
        return string.Join(", ", parts.ToArray());
    }
}

// Familiar Bond — the flat attack bonus a familiar grants its owner. A real
// StatusEffect so the game applies it exactly where vanilla buffs apply
// (SEMan.ModifyAttack runs on every outgoing hit: melee, bows, magic).
// StatusEffect.Clone() is MemberwiseClone, so these fields survive the copy
// SEMan makes on add. Managed by ArmorVfxManager.UpdateFamiliarBuffs.
public class SE_FamiliarBond : StatusEffect
{
    public float m_slash, m_pierce, m_blunt, m_fire, m_frost, m_spirit;

    public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
    {
        hitData.m_damage.m_slash  += m_slash;
        hitData.m_damage.m_pierce += m_pierce;
        hitData.m_damage.m_blunt  += m_blunt;
        hitData.m_damage.m_fire   += m_fire;
        hitData.m_damage.m_frost  += m_frost;
        hitData.m_damage.m_spirit += m_spirit;
    }
}

// Show the aura suffix on the enchanted piece. Per-instance via m_customData,
// so only that piece renames. We prepend a decorated "<name> of <Aura>" line to
// the item tooltip (the surface shown for equipped/inventory armor).
//
// Prepare()-guarded: if the target method can't be resolved on some Valheim
// build, this patch is SKIPPED rather than aborting the whole PatchAll (that
// earlier took down every other patch when the method name was wrong).
[HarmonyPatch]
internal static class ArmorVfxTooltipPatch
{
    private static MethodBase _target;

    private static bool Prepare()
    {
        _target = AccessTools.Method(typeof(ItemDrop.ItemData), "GetTooltip",
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int) });
        if (_target == null)
            Debug.LogWarning("[Valcoin][ArmorVfx] GetTooltip not found — armor rename disabled (visual still works).");
        return _target != null;
    }

    private static MethodBase TargetMethod() => _target;

    private static void Postfix(ItemDrop.ItemData item, ref string __result)
    {
        try
        {
            if (item?.m_customData == null || item.m_shared == null) return;
            if (!item.m_customData.TryGetValue(ArmorVfx.ItemKey, out var a)) return;
            if (!ArmorVfx.Registry.TryGetValue(a, out var aura)) return;

            string baseName = ArmorVfx.LocalizeName(item.m_shared.m_name);
            __result = $"<color=#E8C877>{baseName} {aura.Suffix}</color>\n" + __result;
        }
        catch { /* never break a tooltip */ }
    }
}

// Client-side driver: mirrors the local player's auras to ZDO and renders every
// visible player's auras (self + others). One cheap throttled loop.
public class ArmorVfxManager : MonoBehaviour
{
    private const float Interval = 0.75f;
    private float _next;

    private sealed class Attached { public GameObject Go; public Transform Parent; public string Aura; }
    private readonly Dictionary<string, Attached> _live = new Dictionary<string, Attached>();
    private readonly HashSet<string> _seen = new HashSet<string>();

    private void Update()
    {
        if (Time.time < _next) return;
        _next = Time.time + Interval;
        if (ZNet.instance == null || ZNet.instance.IsServer()) return;

        try { ArmorVfx.MirrorLocalToZdo(); } catch { }
        try { UpdateFamiliarBuffs(); } catch { }

        _seen.Clear();
        List<Player> players = null;
        try { players = Player.GetAllPlayers(); } catch { }
        if (players != null)
            foreach (var p in players)
                RenderPlayer(p);

        // Sweep auras whose owner/slot went away this tick (helmet unequipped,
        // player left) — with a despawn poof at the familiar's last position.
        if (_live.Count > 0)
        {
            var stale = new List<string>();
            foreach (var kv in _live)
                if (!_seen.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (var key in stale)
            {
                var go = _live[key].Go;
                if (go != null) { PlayPoof(go.transform.position); Destroy(go); }
                _live.Remove(key);
            }
        }
    }

    private void RenderPlayer(Player p)
    {
        var nv = ArmorVfx.NView(p);
        if (p == null || nv == null || !nv.IsValid()) return;
        var zdo = nv.GetZDO();
        if (zdo == null) return;

        int pid = p.GetInstanceID();
        foreach (var slot in ArmorVfx.Slots)
        {
            string auraId = "";
            try { auraId = zdo.GetString(ArmorVfx.ZKey(slot), ""); } catch { }
            if (string.IsNullOrEmpty(auraId) || !ArmorVfx.Registry.TryGetValue(auraId, out var def)) continue;

            string key = pid + ":" + slot;
            _seen.Add(key);

            // Familiars hover beside the player — parent to the player root
            // (stable pivot; the offset puts them at the right shoulder, head
            // height) rather than the helmet mesh, which would swing with every
            // head turn.
            var parent = p.transform;

            // Recreate if missing, the aura changed, or the attach point was
            // rebuilt (re-equip swaps the instantiated armor model).
            if (_live.TryGetValue(key, out var cur)
                && (cur.Aura != auraId || cur.Parent != parent || cur.Go == null))
            {
                if (cur.Go != null) PlayPoof(cur.Go.transform.position);
                Destroy(cur.Go);
                _live.Remove(key);
                cur = null;
            }
            else if (!_live.TryGetValue(key, out cur)) cur = null;

            if (cur == null)
            {
                var go = Spawn(def, parent);
                if (go == null) continue;
                cur = new Attached { Go = go, Parent = parent, Aura = auraId };
                _live[key] = cur;
                PlayPoof(go.transform.position);
            }

        }
    }

    // Instantiate the aura's source under `parent` at the companion offset,
    // stripped to a pure looping local visual.
    private GameObject Spawn(ArmorVfx.Aura def, Transform parent)
    {
        var src = ArmorVfx.ResolveSource(def);
        if (src == null) return null;
        try
        {
            var go = def.WholeCreature
                ? SpawnCreatureVisual(src, parent)
                : Instantiate(src, parent);
            if (go == null) return null;

            go.name = "vc_aura_" + def.Id;
            go.transform.localPosition = ArmorVfx.CompanionOffset + new Vector3(0f, def.Raise, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = go.transform.localScale * def.Scale;

            if (!def.WholeCreature)
            {
                StripToVisual(go);
                ForceLoop(go);
            }

            // Unwanted sub-effects (the Gjall's tar drips): destroy matching
            // children outright. Log the particle children once so the hints
            // can be tuned from a live run if the names don't match.
            if (def.StripChildHints != null)
            {
                var psNames = new List<string>();
                foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                {
                    if (tr == null || tr.gameObject == go) continue;
                    if (tr.GetComponent<ParticleSystem>() != null && psNames.Count < 30)
                        psNames.Add(tr.name);
                    var n = tr.name.ToLowerInvariant();
                    foreach (var hint in def.StripChildHints)
                        if (n.Contains(hint)) { Destroy(tr.gameObject); break; }
                }
                Debug.Log($"[Valcoin][ArmorVfx] {def.Id}: particle children: "
                          + string.Join(", ", psNames.ToArray()));
            }

            // Big creatures (Gjall drips, Valkyrie smoke) emit particles sized
            // for the full-size creature. Multiply the emission values down
            // explicitly instead of switching scalingMode — creature rigs carry
            // odd bone scales, so lossyScale-based modes can GROW the effect.
            if (def.TameParticles)
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main;
                    if (main.startSize3D)
                    {
                        main.startSizeXMultiplier *= def.Scale;
                        main.startSizeYMultiplier *= def.Scale;
                        main.startSizeZMultiplier *= def.Scale;
                    }
                    else main.startSizeMultiplier *= def.Scale;
                    main.startSpeedMultiplier *= def.Scale;
                    main.gravityModifierMultiplier *= def.Scale;
                    var shape = ps.shape;
                    if (shape.enabled)
                    {
                        shape.radius *= def.Scale;
                        shape.scale = shape.scale * def.Scale;
                    }
                }

            // Glow auras get a real point light — visible even if the cloned
            // particles turn out to be subtle (the Ghost's glow is partly
            // material emission, which doesn't survive a child clone).
            if (def.HasLight)
            {
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = def.LightColor;
                l.intensity = 1.3f;
                l.range = 1.8f;
                l.shadows = LightShadows.None;
            }

            go.SetActive(true);
            if (def.WholeCreature) TuneAnimators(go);
            return go;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin][ArmorVfx] Spawn '{def.Id}' failed: {ex.Message}");
            return null;
        }
    }

    // Flying creatures idle in a static grounded pose until Character sets the
    // "flying" animator bool — we stripped Character, so set it ourselves (the
    // Hatchling freezes without this). Runs after activation so the Animator is
    // initialized and its parameter list is readable.
    private static void TuneAnimators(GameObject go)
    {
        try
        {
            foreach (var an in go.GetComponentsInChildren<Animator>(true))
            {
                an.applyRootMotion = false;
                an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var p in an.parameters)
                {
                    if (p.type != AnimatorControllerParameterType.Bool) continue;
                    if (p.name == "flying") an.SetBool(p.nameHash, true);
                    else if (p.name == "onGround") an.SetBool(p.nameHash, false);
                }
            }
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                smr.updateWhenOffscreen = true;
        }
        catch { }
    }

    // Clone a creature prefab as a PURE VISUAL mini-pet. The clone is built
    // inside an INACTIVE holder so Awake never runs on its AI/network/physics
    // components — then everything except renderers, particles, animator, LODs
    // and lights is DestroyImmediate'd before the visual is activated. Nothing
    // real (no ZNetView/ZDO, no Character, no colliders) ever wakes up.
    private static readonly HashSet<string> KeepTypes = new HashSet<string>
    {
        "Transform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer",
        "ParticleSystem", "ParticleSystemRenderer", "Animator", "LODGroup", "Light",
    };

    private GameObject SpawnCreatureVisual(GameObject creaturePrefab, Transform parent)
    {
        var holder = new GameObject("vc_familiar_holder");
        holder.SetActive(false);                  // children instantiate un-awakened
        try
        {
            var go = Instantiate(creaturePrefab, holder.transform);
            StripAllExcept(go, KeepTypes);

            // Idle/fly animation without AI: keep the Animator but pin the
            // familiar in place (flying creatures animate with root motion).
            foreach (var an in go.GetComponentsInChildren<Animator>(true))
                an.applyRootMotion = false;

            go.transform.SetParent(parent, false);
            Destroy(holder);
            return go;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Valcoin][ArmorVfx] Creature visual failed: {ex.Message}");
            Destroy(holder);
            return null;
        }
    }

    // Strip every non-whitelisted component in dependency order. Unity's
    // DestroyImmediate logs "Can't remove X because Y depends on it" if a
    // remaining sibling still [RequireComponent]s the target (e.g. CharacterDrop
    // requires Humanoid), so each pass only removes components nothing left
    // depends on, and loops until the tree is clean.
    private static void StripAllExcept(GameObject go, HashSet<string> keep)
    {
        for (int pass = 0; pass < 16; pass++)
        {
            bool leftAny = false, removedAny = false;
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || keep.Contains(comp.GetType().Name)) continue;
                if (RequiredByAnother(comp)) { leftAny = true; continue; }
                try { DestroyImmediate(comp); removedAny = true; } catch { }
            }
            if (!leftAny) break;
            if (!removedAny) break;   // dependency cycle — leave the remainder
        }
    }

    private static bool RequiredByAnother(Component c)
    {
        var t = c.GetType();
        foreach (var other in c.gameObject.GetComponents<Component>())
        {
            if (other == null || ReferenceEquals(other, c)) continue;
            foreach (RequireComponent rc in
                     other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
            {
                if ((rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(t)) ||
                    (rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(t)) ||
                    (rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(t)))
                    return true;
            }
        }
        return false;
    }

    // Keep it a pure local visual: drop anything that would network it, time it
    // out, play audio, or deal damage.
    private static readonly string[] StripTypes =
        { "ZNetView", "ZSyncTransform", "TimedDestruction", "Aoe", "Projectile", "ZSFX", "AudioSource" };

    private static void StripToVisual(GameObject go)
    {
        try
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var n = comp.GetType().Name;
                for (int i = 0; i < StripTypes.Length; i++)
                    if (n == StripTypes[i]) { Destroy(comp); break; }
            }
        }
        catch { }
    }

    // Many source effects are one-shots (spawn/alert/death bursts). Force every
    // particle system to loop so the aura persists.
    private static void ForceLoop(GameObject go)
    {
        try
        {
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.loop = true;
                main.playOnAwake = true;
                ps.Play();
            }
        }
        catch { }
    }

    // ─── familiar buffs while a familiar helmet is equipped ─────────────────
    // Two LOCAL-player effects, both managed on the 0.75s tick:
    //  * feather fall — the Feather Cape's own SlowFall StatusEffect. Wearing
    //    the actual cape too changes nothing (same effect); if the cape is
    //    still on when the helmet comes off, the effect is left alone.
    //  * Familiar Bond — a small flat attack bonus per familiar (SE_FamiliarBond).
    private const string BondName = "VcFamiliarBond";
    private bool _slowFallAdded;
    private string _bondAura;

    // The game hashes SEs by name (StatusEffect.NameHash, in an assembly we
    // don't reference directly) — derive the hash once from a throwaway
    // instance so ours can never drift from the game's algorithm.
    private static int _bondHash;
    private static int BondHash
    {
        get
        {
            if (_bondHash == 0)
            {
                var t = ScriptableObject.CreateInstance<SE_FamiliarBond>();
                t.name = BondName;
                _bondHash = t.NameHash();
                Destroy(t);
            }
            return _bondHash;
        }
    }

    private void UpdateFamiliarBuffs()
    {
        var p = Player.m_localPlayer;
        if (p == null) return;
        var seman = p.GetSEMan();
        if (seman == null) return;

        string aura = ArmorVfx.EquippedAura(p, "head");
        bool want = aura != null;

        // Feather fall (shared with CapeFeather).
        var se = ArmorVfx.SlowFallEffect();
        if (se != null)
        {
            int hash = se.NameHash();
            if (want)
            {
                if (!seman.HaveStatusEffect(hash)) seman.AddStatusEffect(se, true);
                _slowFallAdded = true;
            }
            else if (_slowFallAdded)
            {
                _slowFallAdded = false;
                if (!ArmorVfx.WearsSlowFallItem(p) && seman.HaveStatusEffect(hash))
                    seman.RemoveStatusEffect(hash);
            }
        }

        // Familiar Bond attack bonus. Re-added after death (SEMan clears) and
        // swapped when the equipped familiar changes.
        bool haveBond = seman.HaveStatusEffect(BondHash);
        if (!want)
        {
            if (haveBond) seman.RemoveStatusEffect(BondHash);
            _bondAura = null;
            return;
        }
        if (haveBond && _bondAura != aura)
        {
            seman.RemoveStatusEffect(BondHash);
            haveBond = false;
        }
        if (!haveBond && ArmorVfx.Registry.TryGetValue(aura, out var def))
        {
            var bond = ScriptableObject.CreateInstance<SE_FamiliarBond>();
            bond.name = BondName;
            bond.m_name = $"Familiar Bond ({def.Display})";
            bond.m_tooltip = $"Your {def.Display} familiar sharpens your attacks: {ArmorVfx.StatsText(def)}.";
            bond.m_ttl = 0f;                    // permanent while we manage it
            bond.m_slash = def.Slash;  bond.m_pierce = def.Pierce;
            bond.m_blunt = def.Blunt;  bond.m_fire   = def.Fire;
            bond.m_frost = def.Frost;  bond.m_spirit = def.Spirit;
            seman.AddStatusEffect(bond, true);
        }
        _bondAura = aura;
    }

    // ─── spawn/despawn poof ──────────────────────────────────────────────────
    // A small one-shot burst played where a familiar appears or disappears.
    // Cloned as a pure local visual (same inactive-holder strip as familiars —
    // never instantiate a vfx prefab active, its ZNetView would mint a real
    // networked object). Candidates are all ZNetScene-registered effects.
    private static readonly HashSet<string> PoofKeep = new HashSet<string>
    {
        "Transform", "ParticleSystem", "ParticleSystemRenderer",
        "MeshFilter", "MeshRenderer", "Light",
    };
    private static readonly string[] PoofCandidates = { "vfx_spawn_small", "vfx_spawn", "vfx_ghost_death" };
    private static GameObject _poofPrefab;
    private static bool _poofResolved;

    private static GameObject PoofPrefab()
    {
        if (_poofResolved) return _poofPrefab;
        _poofResolved = true;
        foreach (var n in PoofCandidates)
        {
            var p = ArmorVfx.ResolvePrefab(n);
            if (p != null) { _poofPrefab = p; break; }
        }
        return _poofPrefab;
    }

    private void PlayPoof(Vector3 pos)
    {
        var prefab = PoofPrefab();
        if (prefab == null) return;
        try
        {
            var holder = new GameObject("vc_poof_holder");
            holder.SetActive(false);
            var fx = Instantiate(prefab, holder.transform);
            StripAllExcept(fx, PoofKeep);
            fx.name = "vc_familiar_poof";
            fx.transform.SetParent(null, false);
            fx.transform.position = pos;
            fx.transform.localScale = Vector3.one * 0.6f;   // familiar-sized burst
            foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            fx.SetActive(true);
            Destroy(holder);
            Destroy(fx, 5f);   // one-shot; TimedDestruction was stripped
        }
        catch { }
    }

    private void OnDestroy()
    {
        foreach (var a in _live.Values) if (a?.Go != null) Destroy(a.Go);
        _live.Clear();
    }
}
