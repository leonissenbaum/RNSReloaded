using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;
using RNSReloaded.HyperbolicPlus.Config;
using System.Diagnostics.CodeAnalysis;

using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System;
using System.Security.Cryptography;
using System.Security.AccessControl;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace RNSReloaded.HyperbolicPlus;

/*
    🐀   🐀
🐀           🐀
  "TO ME, MY ALLIES!"
      🎷🐁
🐀           🐀
      🐀
*/

public unsafe class Mod : IMod {
    private const int SCRIPTCONST = 100000;
    public const int ANIM_TIME = 2000;

    private WeakReference<IRNSReloaded>? rnsReloadedRef;
    private WeakReference<IReloadedHooks>? hooksRef;
    private ILoggerV1 logger = null!;

    private Configurator configurator = null!;
    private Config.Config config = null!;
    private Random rng = new Random();

    private int atkNo = 0;
    private double damageMult = 0.0;
    private double gameSpeed = 1.0;
    private int seed = 0;
    private Difficulty diff = Difficulty.LUNAR;
    private int deadPlayers = 0; // bitmask representing dead players
    private bool enFlag = false; // prevent infinite loops
    private bool oneTimeAttacksDone = false; // makes sure karsi's and arinae's circle are activated only once
    private bool isTakingDamage = false; // for invuln control
    private CInstance* musicSelf = null;
    private CInstance* musicOther = null;

    private static Dictionary<string, IHook<ScriptDelegate>> ScriptHooks = [];
    private static readonly string[] PREVENTINVULNSCRIPTS = [
        "scr_pattern_deal_damage_ally",
        "scr_player_invuln",
        "scr_hbsflag_check"
    ];
    private static readonly string[] PERMADEATHSCRIPTS = [
        "scr_kotracker_can_revive",
        "scr_kotracker_draw_timer"
    ];
    private static readonly string[] SPELL_MANIFEST_PATTERNS = [
        "bp_aurum_ghost0_pt2_s",
        "bp_aurum_ghost0_pt3_s",
        "bp_aurum_ghost0_pt4_s",
        "bp_aurum_ghost0_pt5_s",
        "bp_aurum_ghost0_pt6_s",
        "bp_aurum_ghost0_pt7_s",
        "bp_aurum_ghost0_pt8_s",
        "bp_aurum_ghost0_pt2",
        "bp_aurum_ghost0_pt3",
        "bp_aurum_ghost0_pt4",
        "bp_aurum_ghost0_pt5",
        "bp_aurum_ghost0_pt6",
        "bp_aurum_ghost0_pt7",
        "bp_aurum_ghost0_pt8",
    ];

    private IHook<ScriptDelegate>? enemyHookS;
    private IHook<ScriptDelegate>? enemyHookM;
    private List<string> hookedScripts = [];

    public void StartEx(IModLoaderV1 loader, IModConfigV1 modConfig) {
        this.rnsReloadedRef = loader.GetController<IRNSReloaded>()!;
        this.hooksRef = loader.GetController<IReloadedHooks>()!;
        this.logger = loader.GetLogger();

        if (this.rnsReloadedRef.TryGetTarget(out var rnsReloaded)) {
            rnsReloaded.OnReady += this.Ready;
        }

        this.configurator = new Configurator(((IModLoader) loader).GetModConfigDirectory(modConfig.ModId));
        this.config = this.configurator.GetConfiguration<Config.Config>(0);
        this.config.ConfigurationUpdated += this.ConfigurationUpdated;
    }

    private void ConfigurationUpdated(IUpdatableConfigurable newConfig) {
        this.config = (Config.Config) newConfig;
        BattleData.ApplyLiveLengthOverride(this.config);
    }

    public void Ready() {
        if (
            this.rnsReloadedRef != null
            && this.rnsReloadedRef.TryGetTarget(out var rnsReloaded)
            && this.hooksRef != null
            && this.hooksRef.TryGetTarget(out var hooks)
        ) {
            rnsReloaded.LimitOnlinePlay();
            this.InitializeHooks();
        }
    }

    private bool IsReady(
        [MaybeNullWhen(false), NotNullWhen(true)] out IRNSReloaded rnsReloaded,
        [MaybeNullWhen(false), NotNullWhen(true)] out IReloadedHooks hooks,
        [MaybeNullWhen(false), NotNullWhen(true)] out IUtil utils,
        [MaybeNullWhen(false), NotNullWhen(true)] out IBattleScripts scrbp,
        [MaybeNullWhen(false), NotNullWhen(true)] out IBattlePatterns bp
    ) {
        if (
            this.rnsReloadedRef != null
            && this.rnsReloadedRef.TryGetTarget(out rnsReloaded)
            && this.hooksRef != null
            && this.hooksRef.TryGetTarget(out hooks)
        ) {
            utils = rnsReloaded.utils;
            scrbp = rnsReloaded.battleScripts;
            bp = rnsReloaded.battlePatterns;
            return rnsReloaded != null;
        }
        rnsReloaded = null;
        hooks = null;
        utils = null;
        scrbp = null;
        bp = null;
        return false;
    }

    private void CreateAndEnableHook(string scriptName, ScriptDelegate detour, out IHook<ScriptDelegate>? hook) {
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            CScript* script = rnsReloaded.GetScriptData(rnsReloaded.ScriptFindId(scriptName) - SCRIPTCONST);
            hook = hooks.CreateHook(detour, script->Functions->Function);
            hook.Activate();
            hook.Enable();
            return;
        }
        hook = null;
    }

    public void InitializeHooks() {
        var detourMap = new Dictionary<string, ScriptDelegate> {
            { "scr_charselect2_start_run", this.StartRunDetour},
            // damage mult
            { "scr_pattern_deal_damage_enemy_subtract",
                (CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) => {
                    var hook = ScriptHooks["scr_pattern_deal_damage_enemy_subtract"];
                    argv[2]->Real *= this.damageMult;
                    return hook.OriginalFunction(self, other, returnValue, argc, argv);
                }
            },
            // encounters
            { "scr_hallwaygen_outskirts", this.CreateHallwayDetour("scr_hallwaygen_outskirts", NotchType.IntroRoom)},
            { "scr_hallwaygen_outskirts_n", this.CreateHallwayDetour("scr_hallwaygen_outskirts_n", NotchType.IntroRoom)},
            { "scr_hallwaygen_geode", this.CreateHallwayDetour("scr_hallwaygen_geode", NotchType.IntroRoom)},
            { "scr_hallwaygen_toybox", this.CreateHallwayDetour("scr_hallwaygen_toybox", NotchType.ToyboxIntro)},
            // enrage control
            { "bpsw_enrage_time", this.EnrageTimeDetour},
            { "bp_enrage", this.EnrageDetour},
            // invuln control
            { "bp_rabbit_queen1_steel_activate", this.SteelActivateDetour}, // always active
            { "scr_pattern_deal_damage_ally", this.PlayerDmgDetour},
            { "scr_player_invuln", this.InvulnDetour},
            { "scr_hbsflag_check", this.AddHbsFlagCheckDetour},
            // speed control
            { "scrbp_gamespeed", this.GameSpeedDetour},
            // music context capture
            { "scr_music_play", this.CreateMusicContextDetour("scr_music_play")},
            { "scr_music_transfer", this.CreateMusicContextDetour("scr_music_transfer")},
            // permadeath
            { "scr_kotracker_can_revive", this.ReviveDetour},
            { "scr_kotracker_draw_timer", this.KOTimerDetour},
        };

        foreach (var pattern in SPELL_MANIFEST_PATTERNS) {
            detourMap[pattern] = this.CreateSpellManifestPatternDetour(pattern);
        }

        foreach (var detourPair in detourMap) {
            this.CreateAndEnableHook(detourPair.Key, detourPair.Value, out var hook);
            if (hook != null) {
                ScriptHooks[detourPair.Key] = hook;
            }
        }

        this.ConfigSetupHooks();
    }

    private void ConfigSetupHooks() {
        // function to enable/disable certain hooks depending on config
        if (this.config.PreventInvulns) {
            foreach (var script in PREVENTINVULNSCRIPTS) ScriptHooks[script].Enable();
        } else {
            foreach (var script in PREVENTINVULNSCRIPTS) ScriptHooks[script].Disable();
        }
        if (this.config.Permadeath) {
            foreach (var script in PERMADEATHSCRIPTS) ScriptHooks[script].Enable();
        } else {
            foreach (var script in PERMADEATHSCRIPTS) ScriptHooks[script].Disable();
        }
    }

    void execute_pattern(CInstance* self, CInstance* other, string pattern, RValue[] args) {
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            rnsReloaded.ExecuteScript("bpatt_var", self, other, args);
            var x = rnsReloaded.ScriptFindId(pattern);
            args = [new RValue(x)];
            rnsReloaded.ExecuteScript("bpatt_add", self, other, args);
            rnsReloaded.ExecuteScript("bpatt_var_reset", self, other, []);
        }
    }

    private void RunAnimation(Anims id, CInstance* self, CInstance* other) {
        // transform data will eventually be moved to battledata
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            RValue animName;
            RValue[] argv;
            switch (id) {
                case Anims.None:
                    break;
                case Anims.Tassha:
                    this.execute_pattern(self, other, "bp_wolf_disappear", []);
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1000), new RValue(0)]);
                    break;
                case Anims.Karsi:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(0)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_dragon_ruby_big");
                    argv = [animName, new RValue(340), new RValue(2.50), new RValue(0.70)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Center:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(1)]);
                    break;
                case Anims.Twili:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(0)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_bird_valedictorian_big");
                    argv = [animName, new RValue(300), new RValue(2), new RValue(0.50)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Merran:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(1600), new RValue(540), new RValue(1500), new RValue(1)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_wolf_steeltooth_big");
                    argv = [animName, new RValue(380), new RValue(2), new RValue(0.70)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Ranalie:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(1660), new RValue(540), new RValue(1500), new RValue(1)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_dragon_mythril_big");
                    argv = [animName, new RValue(700), new RValue(4), new RValue(1)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Matti:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(1)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_mouse_paladin_big");
                    argv = [animName, new RValue(300), new RValue(2), new RValue(0.50)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Avy:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(0)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_frog_idol_big");
                    argv = [animName, new RValue(300), new RValue(2), new RValue(0.50)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Shira:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(1500), new RValue(1)]);
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_rabbit_queen_big");
                    argv = [animName, new RValue(500), new RValue(2.40), new RValue(0.60)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Farrah:
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_depths_hound_big");
                    argv = [animName, new RValue(500), new RValue(2.80), new RValue(0.75)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.Asha:
                    animName = new RValue(0);
                    rnsReloaded.CreateString(&animName, "anim_aurum_blackcat_big");
                    argv = [animName, new RValue(300), new RValue(1.80), new RValue(0.60)];
                    rnsReloaded.ExecuteScript("scrbp_transform_animation", self, other, argv);
                    break;
                case Anims.SpellManifest:
                    rnsReloaded.ExecuteScript("scrbp_move_character_absolute", self, other, [new RValue(960), new RValue(540), new RValue(0), new RValue(1)]);
                    break;

            }
        }
    }

    private void PlayPatternList(List<string> patterns, CInstance* self, CInstance* other, IUtil utils, IBattleScripts scrbp) {
        int totalLength = BattleData.GetTotalLength(patterns);
        int currTime = 0;
        int totalTime = currTime;
        int currentBag = (this.atkNo) / patterns.Count;
        this.rng = new Random(currentBag + this.seed); // add hallwaySeed to randomize this
        patterns = patterns.OrderBy(x => this.rng.Next()).ToList();
        // play every pattern in bag
        foreach (string pattern in patterns) {
            if (scrbp.time(self, other, ANIM_TIME + totalTime + totalLength * currentBag)) {
                string rpatt = BattleData.GetRealPatternByPattern(pattern, this.diff);
                this.execute_pattern(self, other, rpatt, []);
                this.atkNo++;
                utils.GetGlobalVar("gameTimeSpeed")->Real = this.gameSpeed;
                if (this.config.AccelerateSpeed) {
                    this.gameSpeed += 0.05;
                }
            }
            totalTime += BattleData.GetLengthByPattern(pattern);
        }
        // forces loop
        if (this.atkNo % patterns.Count == 0) {
            scrbp.time(self, other, ANIM_TIME + totalLength * (currentBag + 1));
        }
        currTime += totalLength;
    }

    private void PlayMix(Mixes mix, CInstance* self, CInstance* other, IUtil utils, IBattleScripts scrbp) {
        List<string> patterns = BattleData.GetPatternsByMix(mix);
        this.PlayPatternList(patterns, self, other, utils, scrbp);
    }

    private RValue* StartRunDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var hook = ScriptHooks["scr_charselect2_start_run"];
        // save seed
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            // update config on new run
            this.ConfigSetupHooks();
            RValue diffr = rnsReloaded.ExecuteScript("scr_difficulty", self, other, []) ?? new RValue(0);
            this.diff = (Difficulty) utils.RValueToLong(&diffr); 
            BattleData.ReadConfig(this.config);
            this.deadPlayers = 0; // reset mask
            this.enFlag = false;
            this.oneTimeAttacksDone = false;
            RValue* mapSeedR = rnsReloaded.FindValue(rnsReloaded.GetGlobalInstance(), "mapSeed");
            this.seed = (int) utils.RValueToLong(mapSeedR); // mapSeed is a different datatype for host/client
        }
        return hook!.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private ScriptDelegate CreateHallwayDetour(string scriptName, NotchType introRoom) {
        return (CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) =>
            this.HallwayDetour(scriptName, introRoom, self, other, returnValue, argc, argv);
    }

    private RValue* HallwayDetour(
        string scriptName,
        NotchType introRoom,
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        BattleData.ReadConfig(this.config);
        this.logger.PrintMessage($"Battle name: {Enum.GetName(this.config.ActivePattern)}", System.Drawing.Color.Cyan);
        this.logger.PrintMessage($"Enemy: {BattleData.enemy}", System.Drawing.Color.Cyan);
        var hook = ScriptHooks[scriptName];
        returnValue = hook.OriginalFunction(self, other, returnValue, argc, argv);

        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            string enemy = BattleData.enemy;
            var encounterName = "enc_" + enemy;
            rnsReloaded.utils.setHallway(new List<Notch> {
                new Notch(introRoom, "", 0, 0),
                new Notch(NotchType.Encounter, encounterName != null ? encounterName : "enc_bird_student0", 0, 0),
                new Notch(NotchType.EndRun, "", 0, 0)
            }, self, rnsReloaded);

            // hook into the right scripts given the difficulty
            if (!this.hookedScripts.Contains("bp_" + enemy + "_s")) {
                CScript* enemyScriptS = rnsReloaded.GetScriptData(rnsReloaded.ScriptFindId("bp_" + enemy + "_s") - SCRIPTCONST);
                this.enemyHookS = hooks.CreateHook<ScriptDelegate>(this.EnemyDetour, enemyScriptS->Functions->Function);
                this.enemyHookS.Activate();
                this.enemyHookS.Enable();
                this.hookedScripts.Add("bp_" + enemy + "_s");
            }
            if (!this.hookedScripts.Contains("bp_" + enemy)) {
                CScript* enemyScriptM = rnsReloaded.GetScriptData(rnsReloaded.ScriptFindId("bp_" + enemy) - SCRIPTCONST);
                this.enemyHookM = hooks.CreateHook<ScriptDelegate>(this.EnemyMDetour, enemyScriptM->Functions->Function);
                this.enemyHookM.Activate();
                this.enemyHookM.Enable();
                this.hookedScripts.Add("bp_" + enemy);
            }

            // set level, the game adds 2 levels after so 997 gets us 999
            var enemyLevel = rnsReloaded.FindValue(rnsReloaded.GetGlobalInstance(), "enemyLevel");
            *enemyLevel = new RValue(997);

        }
        this.enFlag = false;
        this.oneTimeAttacksDone = false;
        
        return returnValue;
    }

    private RValue* EnemyMDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        bool basic = BattleData.basic;
        if (this.enFlag && basic) {
            if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
                if (scrbp.time_repeating(self, other, 0, BattleData.length)) {
                    // accelerate speed
                    utils.GetGlobalVar("gameTimeSpeed")->Real = this.gameSpeed;
                    if (this.config.AccelerateSpeed) {
                        this.gameSpeed += 0.1;
                    }
                }
            }
            // basic only works for solo
            if (this.enemyHookS != null) return this.enemyHookS.OriginalFunction(self, other, returnValue, argc, argv);
            else return returnValue;
        }
        return this.EnemyDetour(self, other, returnValue, argc, argv);
    }

    private RValue* EnemyDetour(
        CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            if (this.enFlag && BattleData.basic) {
                if (scrbp.time_repeating(self, other, 0, BattleData.length)) {
                    // accelerate speed
                    utils.GetGlobalVar("gameTimeSpeed")->Real = this.gameSpeed;
                    if (this.config.AccelerateSpeed) {
                        this.gameSpeed += 0.1;
                    }
                }
                // is calling different function the second time
                // check if this is case for boss/midboss
                if (this.enemyHookS != null) return this.enemyHookS.OriginalFunction(self, other, returnValue, argc, argv);
                else {
                    return returnValue;
                }
            }
            this.enFlag = true;

            if (scrbp.time(self, other, 0)) {
                // change environment
                this.RunAnimation(BattleData.anim, self, other);
                rnsReloaded.ExecuteScript("scrbp_zoom", self, other, [new RValue(BattleData.zoom)]);
                rnsReloaded.ExecuteScript("scr_stage_change", self, other, [new RValue(BattleData.stage)]);
                rnsReloaded.ExecuteScript("scr_stage_play_music", self, other, [new RValue(BattleData.stage)]);
                CInstance* musicCallSelf = this.musicSelf != null ? this.musicSelf : self;
                CInstance* musicCallOther = this.musicOther != null ? this.musicOther : other;
                if (BattleData.music != 0) {
                    rnsReloaded.ExecuteScript("scr_music_play", musicCallSelf, musicCallOther, [new RValue(BattleData.music)]);
                } else {
                    // change from the out of battle theme to the in battle theme
                    rnsReloaded.ExecuteScript("scr_music_transfer", musicCallSelf, musicCallOther, [new RValue(true)]);
                }
                if (!this.oneTimeAttacksDone &&
                    BattleData.enemy == "depths_angel0" &&
                    !BattleData.pattern.StartsWith("bp_depths_angel0_pt2")) {
                    this.execute_pattern(self, other, "bp_depths_angel0_quickstart", []);
                    this.oneTimeAttacksDone = true;
                }
                // set tracking variables
                this.atkNo = 0;
                this.gameSpeed = utils.GetGlobalVar("gameTimeSpeed")->Real;
            }

            if (BattleData.anim == Anims.Karsi && !this.oneTimeAttacksDone && scrbp.time(self, other, ANIM_TIME)) {
                this.execute_pattern(self, other, "bp_dragon_ruby0_perm", []);
                this.oneTimeAttacksDone = true;
            }

            // check if play mix
            Mixes mix = BattleData.mix;
            if (mix != Mixes.None) {
                this.PlayMix(mix, self, other, utils, scrbp);
                return returnValue;
            }

            if (BattleData.basic) {
                rnsReloaded.ExecuteScript(BattleData.GetRealPattern(this.diff), self, other, argc, argv);
            }

            else if (scrbp.time_repeating(self, other, ANIM_TIME, BattleData.length)) {
                // call pattern on set loop
                if (!BattleData.basic) {
                    this.execute_pattern(self, other, BattleData.GetRealPattern(this.diff), []);
                }

                // accelerate speed
                utils.GetGlobalVar("gameTimeSpeed")->Real = this.gameSpeed;
                if (this.config.AccelerateSpeed) {
                    this.gameSpeed += 0.1;
                }
            }
        }

        return returnValue;
    }

    private ScriptDelegate CreateMusicContextDetour(string scriptName) {
        return (CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) =>
            this.MusicContextDetour(scriptName, self, other, returnValue, argc, argv);
    }

    private RValue* MusicContextDetour(string scriptName, CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        // when playing music the game cares about the current self and other instances, if it's wrong then there's a crash
        // and if we just force the values onto a different instance, multiple tracks play at once
        // so i just copy the instances here so we can play onto the original ones
        // very bad way of doing it but if it works it works
        var hook = ScriptHooks[scriptName];

        this.musicSelf = self;
        this.musicOther = other;

        return hook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* EnrageTimeDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var a = new RValue(-1);
        return &a;
    }

    // for whatever reason spell manifest enrages regardless of the other enrage blocker
    private RValue* EnrageDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        if (BattleData.enemy == "aurum_ghost0") {
            return returnValue;
        }

        var hook = ScriptHooks["bp_enrage"];
        return hook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    // spell manifest patterns, unlike regular patterns, do not follow the base enemy script, instead they follow a pattern of
    // damage spell manifest -> split into four -> repeat
    // the issue is the split into four parts, hyperbolicplus breaks on those if we don't do anything, it just never repeats
    // we deal with this by overwriting calls to damage spell manifest and making them just repeat the split into four patterns
    // for more info, see https://discord.com/channels/496640298844422149/1239731124952105100/1521433018189877319
    private ScriptDelegate CreateSpellManifestPatternDetour(string pattern) {
        return (CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) => {
            string selectedPattern = BattleData.GetRealPattern(this.diff);
            if (BattleData.enemy == "aurum_ghost0" && pattern != selectedPattern) {
                if (ScriptHooks.TryGetValue(selectedPattern, out var selectedHook)) {
                    return selectedHook.OriginalFunction(self, other, returnValue, argc, argv);
                }

                return returnValue;
            }

            var hook = ScriptHooks[pattern];
            return hook.OriginalFunction(self, other, returnValue, argc, argv);
        };
    }

    // INVULN CONTROL
    private RValue* SteelActivateDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        // disables steel yourself attack, should always be active
        return returnValue;
    }

    private RValue* PlayerDmgDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var hook = ScriptHooks["scr_pattern_deal_damage_ally"];
        // keep track for hit invuln
        this.isTakingDamage = true;
        returnValue = hook.OriginalFunction(self, other, returnValue, argc, argv);
        this.isTakingDamage = false;
        return returnValue;
    }

    private RValue* InvulnDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var hook = ScriptHooks["scr_player_invuln"];
        // this is basically steelheart's implementation
        if (!this.isTakingDamage) { argv[0]->Real = -30000; }
        return hook.OriginalFunction(self, other, returnValue, argc, argv);
    }

    private RValue* AddHbsFlagCheckDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var hook = ScriptHooks["scr_hbsflag_check"];
        returnValue = hook.OriginalFunction(self, other, returnValue, argc, argv);
        if (argv[2]->Real == 1 || argv[2]->Real == 2 || argv[2]->Real == 32) { // Vanish/Ghost, Stoneskin, Super
            returnValue->Real = 0;
        }
        return returnValue;
    }

    // PERMADEATH CONTROL
    private RValue* ReviveDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        // canRevive is false
        RValue result = new RValue(0);
        return &result;
    }

    // GAMESPEED CONTROL
    private RValue* GameSpeedDetour(
    CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv
    ) {
        var hook = ScriptHooks["scrbp_gamespeed"];
        (*argv)->Real *= this.gameSpeed;
        returnValue = hook!.OriginalFunction(self, other, returnValue, argc, argv);
        return returnValue;
    }

    // PLAYER DEATH LOGGER
    private RValue* KOTimerDetour(CInstance* self, CInstance* other, RValue* returnValue, int argc, RValue** argv) {
        var hook = ScriptHooks["scr_kotracker_draw_timer"];
        if (this.IsReady(out var rnsReloaded, out var hooks, out var utils, out var scrbp, out var bp)) {
            // if KOtimer is drawn, add player to mask
            int id = (int) utils.RValueToLong(argv[0]);
            if ((this.deadPlayers & (1 << id)) == 0) { // player hasn't been marked dead yet
                Console.WriteLine($"Player {id} has just fallen");
                this.deadPlayers |= (1 << id); // set the bit for player
            }
        }
        return hook!.OriginalFunction(self, other, returnValue, argc, argv);
    }

    public void Suspend() { }

    public void Resume() { }

    public bool CanSuspend() => false; // Add suspend/resume code and set to true once ready

    public void Unload() { }
    public bool CanUnload() => false;

    public Action Disposing => () => { };
}
