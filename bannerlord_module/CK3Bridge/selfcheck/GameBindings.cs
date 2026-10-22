using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace CK3Bridge.SelfCheck
{
    /// <summary>
    /// Metadata-only checks against the installed game (Assembly.ReflectionOnlyLoadFrom: nothing is executed, no engine
    /// is needed). They pin the assumptions the module makes about private game members and about its own wiring.
    /// </summary>
    internal static class GameBindings
    {
        public static void Run(string moduleDir, string bannerlordBin, Action<bool, string> check)
        {
            string root = Path.GetFullPath(Path.Combine(bannerlordBin, "..", ".."));
            string moduleBin = Path.Combine(moduleDir, "bin", "Win64_Shipping_Client");
            string[] searchDirs =
            {
                bannerlordBin,
                Path.Combine(root, "Modules", "Native", "bin", "Win64_Shipping_Client"),
                Path.Combine(root, "Modules", "CustomBattle", "bin", "Win64_Shipping_Client"),
                Path.Combine(root, "Modules", "Multiplayer", "bin", "Win64_Shipping_Client"),
                moduleBin,
            };
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += (_, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                foreach (string dir in searchDirs)
                {
                    string path = Path.Combine(dir, name + ".dll");
                    if (File.Exists(path)) return Assembly.ReflectionOnlyLoadFrom(path);
                }
                return Assembly.ReflectionOnlyLoad(AppDomain.CurrentDomain.ApplyPolicy(args.Name));
            };

            // Private members reached by reflection (CK3BridgeSubModule, CK3Missions).
            Assembly mountAndBlade = Assembly.ReflectionOnlyLoadFrom(Path.Combine(bannerlordBin, "TaleWorlds.MountAndBlade.dll"));
            FieldInfo splash = mountAndBlade.GetType("TaleWorlds.MountAndBlade.Module", true)
                .GetField("_splashScreenPlayed", BindingFlags.Instance | BindingFlags.NonPublic);
            check(splash != null && splash.FieldType.FullName == typeof(bool).FullName, "Module._splashScreenPlayed is a private bool");

            Assembly view = Assembly.ReflectionOnlyLoadFrom(Path.Combine(searchDirs[1], "TaleWorlds.MountAndBlade.View.dll"));
            FieldInfo creators = view.GetType("TaleWorlds.MountAndBlade.View.ViewCreatorManager", true)
                .GetField("_viewCreators", BindingFlags.Static | BindingFlags.NonPublic);
            check(creators != null && creators.FieldType.Name.StartsWith("Dictionary`2", StringComparison.Ordinal),
                "ViewCreatorManager._viewCreators is a static dictionary");

            // Our own wiring.
            var subModule = new XmlDocument();
            subModule.Load(Path.Combine(moduleDir, "SubModule.xml"));
            string classType = subModule.SelectSingleNode("/Module/SubModules/SubModule/SubModuleClassType/@value")?.Value;
            string dllName = subModule.SelectSingleNode("/Module/SubModules/SubModule/DLLName/@value")?.Value;
            string dllPath = Path.Combine(moduleBin, dllName ?? "?");
            check(File.Exists(dllPath), "SubModule.xml DLLName exists in bin/Win64_Shipping_Client");
            if (!File.Exists(dllPath)) return;

            Assembly ours = Assembly.ReflectionOnlyLoadFrom(dllPath);
            check(classType != null && ours.GetType(classType) != null, "SubModule.xml SubModuleClassType exists in the DLL");

            AssemblyName viewReference = ours.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "TaleWorlds.MountAndBlade.View");
            check(viewReference != null && viewReference.FullName == view.GetName().FullName,
                "CK3Bridge references the installed View assembly (ViewCreatorManager scans by exact name)");

            Type views = ours.GetType("CK3Bridge.CK3BattleViews", true);
            check(CustomAttributeData.GetCustomAttributes(views).Any(a => a.AttributeType.Name == "ViewCreatorModule"),
                "CK3BattleViews has [ViewCreatorModule]");
            MethodInfo open = views.GetMethod("OpenCK3Battle", BindingFlags.Static | BindingFlags.Public);
            CustomAttributeData viewMethod = open == null ? null
                : CustomAttributeData.GetCustomAttributes(open).FirstOrDefault(a => a.AttributeType.Name == "ViewMethod");
            check(viewMethod != null && (string)viewMethod.ConstructorArguments[0].Value == "CK3Battle", "[ViewMethod(\"CK3Battle\")] is registered");
            MethodInfo openSiege = views.GetMethod("OpenCK3Siege", BindingFlags.Static | BindingFlags.Public);
            CustomAttributeData siegeMethod = openSiege == null ? null
                : CustomAttributeData.GetCustomAttributes(openSiege).FirstOrDefault(a => a.AttributeType.Name == "ViewMethod");
            check(siegeMethod != null && (string)siegeMethod.ConstructorArguments[0].Value == "CK3Siege", "[ViewMethod(\"CK3Siege\")] is registered");
            CheckViewCreatorModules(ours, check);

            // Siege engines by name (CK3Missions.SiegeWeapons; the bridge's SiegePlanner names them, PROTOCOL.md §39).
            Assembly core = Assembly.ReflectionOnlyLoadFrom(Path.Combine(bannerlordBin, "TaleWorlds.Core.dll"));
            Type engines = core.GetType("TaleWorlds.Core.DefaultSiegeEngineTypes", true);
            foreach (string engine in new[] { "SiegeTower", "Ram", "Onager", "FireOnager", "Catapult", "Trebuchet", "FireTrebuchet", "Ballista" })
                check(engines.GetProperty(engine, BindingFlags.Public | BindingFlags.Static) != null, $"DefaultSiegeEngineTypes.{engine} exists");

            Type screen = ours.GetType("CK3Bridge.CK3IdleScreen", true);
            CustomAttributeData screenFor = CustomAttributeData.GetCustomAttributes(screen).FirstOrDefault(a => a.AttributeType.Name == "GameStateScreen");
            check(screenFor != null && ((Type)screenFor.ConstructorArguments[0].Value).FullName == "CK3Bridge.CK3IdleState",
                "CK3IdleScreen is the [GameStateScreen] of CK3IdleState");
            check(screen.GetConstructors().Any(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.FullName == "CK3Bridge.CK3IdleState"),
                "CK3IdleScreen has the (CK3IdleState) constructor GameStateScreenManager calls");
            check(File.Exists(Path.Combine(moduleDir, "GUI", "Prefabs", "CK3IdleScreen.xml")), "GUI/Prefabs/CK3IdleScreen.xml exists");

            Type tracker = ours.GetType("CK3Bridge.BattleTracker", true);
            check(tracker.GetInterfaces().Any(i => i.FullName == "TaleWorlds.MountAndBlade.IAgentStateDecider"),
                "BattleTracker implements IAgentStateDecider");

            // Presentation layer (stage 2).
            Assembly gauntlet = Assembly.ReflectionOnlyLoadFrom(Path.Combine(bannerlordBin, "TaleWorlds.GauntletUI.dll"));
            FieldInfo providers = gauntlet.GetType("TaleWorlds.GauntletUI.TextureProviderFactory", true)
                .GetField("_textureProvidertypes", BindingFlags.Static | BindingFlags.NonPublic);
            check(providers != null && providers.FieldType.Name.StartsWith("Dictionary`2", StringComparison.Ordinal),
                "TextureProviderFactory._textureProvidertypes is a static dictionary");
            foreach (string name in new[] { "CK3PlayerCoaTextureProvider", "CK3EnemyCoaTextureProvider" })
            {
                Type provider = ours.GetType("CK3Bridge." + name, true);
                check(provider.IsPublic && !provider.IsAbstract && InheritsFrom(provider, "TaleWorlds.GauntletUI.TextureProvider")
                      && provider.GetConstructor(Type.EmptyTypes) != null,
                    name + " is a public TextureProvider with a parameterless constructor (TextureProviderFactory.CreateInstance)");
            }

            Type loading = ours.GetType("CK3Bridge.CK3LoadingWindowManager", true);
            check(loading.GetInterfaces().Any(i => i.FullName == "TaleWorlds.Engine.ILoadingWindowManager")
                  && InheritsFrom(loading, "TaleWorlds.ScreenSystem.GlobalLayer") && loading.GetConstructor(Type.EmptyTypes) != null,
                "CK3LoadingWindowManager is a GlobalLayer + ILoadingWindowManager with a parameterless constructor (LoadingWindow.InitializeWith<T>)");
            check(InheritsFrom(ours.GetType("CK3Bridge.CK3ScoreboardVM", true), "TaleWorlds.MountAndBlade.ViewModelCollection.Scoreboard.CustomBattleScoreboardVM"),
                "CK3ScoreboardVM extends the stock custom battle scoreboard view model");
            check(InheritsFrom(ours.GetType("CK3Bridge.CK3HandoffView", true), "TaleWorlds.MountAndBlade.View.MissionViews.MissionView"),
                "CK3HandoffView is a MissionView");

            // Combat buffs (PROTOCOL.md §13.4): the three models replace the CustomBattle ones they derive from, the
            // submodule registers them in OnGameStart, and the captains logic overrides the two stock extension points.
            CheckOverrides(ours, check, "CK3Bridge.CK3AgentStatCalculateModel", "TaleWorlds.MountAndBlade.CustomBattleAgentStatCalculateModel",
                "UpdateAgentStats", "GetWeaponInaccuracy", "GetKnockDownResistance", "GetKnockBackResistance", "GetDismountResistance");
            CheckOverrides(ours, check, "CK3Bridge.CK3AgentApplyDamageModel", "TaleWorlds.MountAndBlade.CustomAgentApplyDamageModel",
                "ApplyGeneralDamageModifiers");
            CheckOverrides(ours, check, "CK3Bridge.CK3BattleMoraleModel", "TaleWorlds.MountAndBlade.CustomBattleMoraleModel",
                "GetEffectiveInitialMorale", "CanPanicDueToMorale");
            CheckOverrides(ours, check, "CK3Bridge.CK3BridgeSubModule", "TaleWorlds.MountAndBlade.MBSubModuleBase", "OnGameStart");
            CheckOverrides(ours, check, "CK3Bridge.CK3CaptainsAssignmentLogic", "TaleWorlds.MountAndBlade.GeneralsAndCaptainsAssignmentLogic",
                "SortCaptainsByPriority", "PickBestRegularFormationToLead");

            // CaptainFormation is cast to FormationClass: same names, same values.
            Type formationClass = Assembly.ReflectionOnlyLoadFrom(Path.Combine(bannerlordBin, "TaleWorlds.Core.dll")).GetType("TaleWorlds.Core.FormationClass", true);
            foreach (FieldInfo field in ours.GetType("CK3Bridge.CaptainFormation", true).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object engine = formationClass.GetField(field.Name)?.GetRawConstantValue();
                check(engine != null && Convert.ToInt32(engine) == Convert.ToInt32(field.GetRawConstantValue()),
                    $"CaptainFormation.{field.Name} = FormationClass.{field.Name}");
            }
        }

        /// <summary>
        /// <paramref name="typeName"/> derives from <paramref name="baseFullName"/> and declares an override of each named member
        /// (a virtual method of its own whose base definition lies in a base class, not a new slot).
        /// </summary>
        private static void CheckOverrides(Assembly ours, Action<bool, string> check, string typeName, string baseFullName, params string[] members)
        {
            Type type = ours.GetType(typeName, true);
            check(InheritsFrom(type, baseFullName), $"{type.Name} derives from {baseFullName}");
            foreach (string member in members)
            {
                bool overrides = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Any(m => m.Name == member && m.IsVirtual && m.GetBaseDefinition().DeclaringType != type);
                check(overrides, $"{type.Name} overrides {member}");
            }
        }

        /// <summary>
        /// Mirrors ViewCreatorManager.CheckAssemblyScreens (View ViewCreatorManager.cs, run from its static constructor for
        /// every assembly that references the View assembly): for each [ViewCreatorModule] type it reads attribute [0] of
        /// GetMethods(Static | Public | NonPublic) without a length check, so one static method without [ViewMethod]
        /// (a helper, or a compiler-generated local function) throws there and breaks every mission's views for good.
        /// </summary>
        private static void CheckViewCreatorModules(Assembly ours, Action<bool, string> check)
        {
            foreach (Type type in ours.GetTypes())
            {
                if (!CustomAttributeData.GetCustomAttributes(type).Any(a => a.AttributeType.Name == "ViewCreatorModule")) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    int viewMethods = CustomAttributeData.GetCustomAttributes(method).Count(a => a.AttributeType.Name == "ViewMethod");
                    check(viewMethods == 1, $"static {type.Name}.{method.Name} of a [ViewCreatorModule] carries exactly one [ViewMethod] (has {viewMethods})");
                }
            }
        }

        private static bool InheritsFrom(Type type, string baseFullName)
        {
            for (Type t = type.BaseType; t != null; t = t.BaseType)
                if (t.FullName == baseFullName) return true;
            return false;
        }
    }
}
