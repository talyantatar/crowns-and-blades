using System;
using CK3xBL.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>request.json -> validated BattleRequest + extras + normalised opener inputs.</summary>
    internal static class RequestIntake
    {
        public static BattleRequest Parse(string json, out RequestExtras extras)
        {
            JObject root;
            try
            {
                root = ProtocolJson.ParseObject(json);
            }
            catch (JsonException e)
            {
                throw new RequestException("request.json is not valid JSON: " + e.Message);
            }
            BattleRequest request;
            try
            {
                request = ProtocolJson.ToObject<BattleRequest>(root);
            }
            catch (JsonException e)
            {
                throw new RequestException("request.json does not match protocol 2: " + e.Message);
            }
            // A missing "protocol" keeps the DTO default (2): protocol 1 requests are a compatible subset.
            if (request.Protocol != ProtocolInfo.Version)
                throw new RequestException($"unsupported protocol {request.Protocol} (this module speaks {ProtocolInfo.Version})");
            if (string.IsNullOrWhiteSpace(request.Id)) throw new RequestException("request has no id");
            if (request.Presentation == null) request.Presentation = new Presentation();
            if (request.Scale == null) request.Scale = new ScaleInfo();
            extras = RequestExtras.From(root);
            // Only an explicit "preload": true holds the battle until "go" (the DTO default would hold every request
            // that omits it, e.g. protocol 1 requests without presentation).
            request.Presentation.Preload = extras.Preload;
            return request;
        }

        public static MissionSetup Plan(BattleRequest request, RequestExtras extras)
        {
            // Every combat is a Bannerlord field battle, but for a noble single combat in an arena (PROTOCOL.md §27), a skirmish at a
            // hideout (§37) and an assault on walls (§39), each falling back to its field scene when its own cannot be loaded.
            bool arena = string.Equals(request.Kind, "arena", StringComparison.OrdinalIgnoreCase);
            if (arena && !(CK3Missions.IsArenaScene(request.Scene) && CK3Missions.IsArenaMissionReady()))
            {
                BridgeIo.Log($"arena '{request.Scene}' cannot be opened (SandBox not loaded?); the duel is fought on {request.FallbackScene ?? RequestRules.DefaultFieldScene}");
                arena = false;
                request.Scene = request.FallbackScene;
            }
            bool hideout = string.Equals(request.Kind, "hideout", StringComparison.OrdinalIgnoreCase);
            if (hideout && !(CK3Missions.IsHideoutScene(request.Scene) && CK3Missions.IsArenaMissionReady()))
            {
                BridgeIo.Log($"hideout '{request.Scene}' cannot be opened (SandBox not loaded?); the skirmish is fought on {request.FallbackScene ?? RequestRules.DefaultFieldScene}");
                hideout = false;
                request.Scene = request.FallbackScene;
            }
            if (hideout)
            {
                return new MissionSetup
                {
                    Scene = request.Scene,
                    Season = RequestRules.Season(request.Season),
                    PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                    SceneLevels = extras.SceneLevels ?? "",
                    Extras = extras,
                    BattleSizeIndex = BannerlordConfig.BattleSize,
                    Hideout = true,
                };
            }
            bool siege = string.Equals(request.Kind, "siege", StringComparison.OrdinalIgnoreCase) && request.Siege != null;
            if (siege && !(CK3Missions.IsSiegeScene(request.Scene) && CK3Missions.IsSiegeMissionReady()))
            {
                BridgeIo.Log($"siege scene '{request.Scene}' cannot be opened; the assault is fought on {request.FallbackScene ?? RequestRules.DefaultFieldScene}");
                siege = false;
                request.Scene = request.FallbackScene;
            }
            if (siege)
            {
                return new MissionSetup
                {
                    Scene = request.Scene,
                    Season = RequestRules.Season(request.Season),
                    PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                    SceneLevels = "",
                    Extras = extras,
                    BattleSizeIndex = extras.BattleSize.HasValue ? RequestRules.BattleSizeIndex(extras.BattleSize.Value) : BannerlordConfig.BattleSize,
                    Siege = request.Siege,
                };
            }
            bool naval = string.Equals(request.Kind, "naval", StringComparison.OrdinalIgnoreCase) && request.Naval != null;
            if (naval && !(CK3Missions.NavalOpener != null && Utilities.TryGetFullFilePathOfScene(request.Scene ?? "", out _)))
            {
                BridgeIo.Log($"naval scene '{request.Scene}' cannot be opened (War Sails not loaded?); the battle is fought on {request.FallbackScene ?? RequestRules.DefaultFieldScene}");
                naval = false;
                request.Scene = request.FallbackScene;
            }
            if (naval && request.Naval.Raid && !RequestRules.IsPlayerAttacker(request.PlayerSide))
            {
                // War Sails' raid lands the player's own ships; with the player ashore its controller crashes (live b298, bench).
                BridgeIo.Log($"a landing the player defends is fought on {request.FallbackScene ?? RequestRules.DefaultFieldScene}");
                naval = false;
                request.Scene = request.FallbackScene;
            }
            if (naval)
            {
                return new MissionSetup
                {
                    Scene = request.Scene,
                    Season = RequestRules.Season(request.Season),
                    PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                    SceneLevels = "",
                    Extras = extras,
                    BattleSizeIndex = extras.BattleSize.HasValue ? RequestRules.BattleSizeIndex(extras.BattleSize.Value) : BannerlordConfig.BattleSize,
                    Naval = request.Naval,
                };
            }
            if (!arena && !string.Equals(request.Kind ?? "field", "field", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.Kind, "hideout", StringComparison.OrdinalIgnoreCase))
                BridgeIo.Log($"kind '{request.Kind}' is fought as a field battle");

            if (arena)
            {
                return new MissionSetup
                {
                    Scene = request.Scene,
                    Season = RequestRules.Season(request.Season),
                    PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                    SceneLevels = extras.SceneLevels ?? "",
                    Extras = extras,
                    BattleSizeIndex = BannerlordConfig.BattleSize,
                    Arena = true,
                    Tourney = request.Tourney,
                    TourneyPar = request.TourneyPar,
                    ArcheryHitFormat = request.Presentation?.Language == "ru" ? "Попаданий: {0} из {1}" : "Hits: {0} of {1}",
                };
            }

            var setup = new MissionSetup
            {
                Scene = request.Scene,
                Season = RequestRules.Season(request.Season),
                PresetHour = RequestRules.PresetHour(request.TimeOfDay),
                SceneLevels = extras.SceneLevels ?? "",
                Extras = extras,
                // Without scale.battleSize the user's own battle size option stays in effect.
                BattleSizeIndex = extras.BattleSize.HasValue ? RequestRules.BattleSizeIndex(extras.BattleSize.Value) : BannerlordConfig.BattleSize,
            };
            if (!CK3Missions.IsFieldScene(setup.Scene, out string forcedLevel, out string reason))
            {
                BridgeIo.Log($"scene '{setup.Scene}' is {reason}; using {RequestRules.DefaultFieldScene}");
                setup.Scene = RequestRules.DefaultFieldScene;
                setup.SceneLevels = "";
            }
            else if (setup.SceneLevels.Length == 0 && forcedLevel != null)
            {
                setup.SceneLevels = forcedLevel;
            }
            return setup;
        }
    }
}
