using HarmonyLib;
using IPA.Loader;
using SiraUtil.Affinity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PracticePlugin.ScoreSaberPatch
{
    [HarmonyPatch]
    internal class FixDictionary
    {
        private static FieldInfo s_fieldInfo = null;
        private static readonly Type[] s_argumentTypes = new Type[] { typeof(NoteController), typeof(NoteCutInfo).MakeByRefType() };
        private static Assembly s_discoveryAssembly;
        private static Task<DiscoveryResult> s_discoveryTask;
        private static MethodBase s_preparedMethod;
        private static bool s_discoveryPublished;

        internal sealed class DiscoveryResult
        {
            internal readonly MethodBase Method;
            internal readonly FieldInfo Field;

            internal DiscoveryResult(MethodBase method, FieldInfo field)
            {
                Method = method;
                Field = field;
            }
        }

        internal static Task<DiscoveryResult> PrepareDiscovery(bool logUnavailable = true)
        {
            Assembly assembly = GetScoreSaberAssembly(logUnavailable);
            if (assembly == null) return Task.FromResult<DiscoveryResult>(null);
            if (s_discoveryTask != null && assembly == s_discoveryAssembly &&
                !s_discoveryTask.IsFaulted && !s_discoveryTask.IsCanceled) return s_discoveryTask;

            Task<DiscoveryResult> previous = s_discoveryTask;
            s_discoveryAssembly = assembly;
            s_discoveryPublished = false;
            s_discoveryTask = previous == null
                ? Task.Factory.StartNew(Discover, assembly, CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                : previous.ContinueWith(completed => {
                    if (completed.IsFaulted) _ = completed.Exception;
                    return Discover(assembly);
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return s_discoveryTask;
        }

        internal static void PublishDiscovery(DiscoveryResult result)
        {
            s_preparedMethod = result?.Method;
            if (s_fieldInfo == null) s_fieldInfo = result?.Field;
            s_discoveryPublished = true;
            if (result != null && result.Method == null) Logger.Info("Not found target method.");
        }

        /// <summary>
        /// パッチを当てるかどうか
        /// </summary>
        /// <param name="original"></param>
        /// <returns></returns>
        [HarmonyPrepare]
        public static bool AddNoteDataPrepare(MethodBase original)
        {
            return AddNoteDataMethod(original) != null;
        }

        /// <summary>
        /// ScoreSaber.dllから対象のメソッド情報を取得します。
        /// </summary>
        /// <param name="original"></param>
        /// <returns></returns>
        [HarmonyTargetMethod]
        public static MethodBase AddNoteDataMethod(MethodBase original)
        {
            if (original != null) {
                return original;
            }
            if (s_discoveryPublished) return s_preparedMethod;
            if (s_discoveryTask != null && s_discoveryTask.Status == TaskStatus.RanToCompletion) {
                PublishDiscovery(s_discoveryTask.GetAwaiter().GetResult());
                return s_preparedMethod;
            }
            Assembly scoreSaberAssembly = GetScoreSaberAssembly(true);
            if (scoreSaberAssembly == null) return null;
            DiscoveryResult result = Discover(scoreSaberAssembly);
            PublishDiscovery(result);
            return result.Method;
        }

        private static Assembly GetScoreSaberAssembly(bool logUnavailable)
        {
            var scoreSaberInfo = PluginManager.GetPlugin("ScoreSaber");
            if (scoreSaberInfo == null) {
                if (logUnavailable) Logger.Info("ScoreSaber not loaded.");
                return null;
            }
            var scoresaberPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "ScoreSaber.dll");
            Assembly scoreSaberAssembly = null;
            try {
                scoreSaberAssembly = Assembly.LoadFrom(scoresaberPath);
            }
            catch (FileNotFoundException) {
                if (logUnavailable) Logger.Info("ScoreSaber failed load");
                return null;
            }
            catch (Exception e) {
                if (logUnavailable) Logger.Error(e);
                return null;
            }
            return scoreSaberAssembly;
        }

        private static DiscoveryResult Discover(object state)
        {
            Assembly scoreSaberAssembly = (Assembly)state;
            var affinies = scoreSaberAssembly.GetTypes().Where(x => typeof(IAffinity).IsAssignableFrom(x) && x.IsClass && !x.IsAbstract && !x.IsInterface);
            foreach (var affinityType in affinies) {
                var methodInfos = affinityType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(x => x.GetCustomAttribute(typeof(AffinityPatchAttribute)) != null);

                foreach (var methodInfo in methodInfos) {
                    var arguments = methodInfo.GetParameters().Select(x => x.ParameterType).ToArray();
                    if (arguments.SequenceEqual(s_argumentTypes)) {
                        FieldInfo field = affinityType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(x => x.FieldType.Equals(typeof(Dictionary<NoteData, NoteCutInfo>)));
                        return new DiscoveryResult(methodInfo, field);
                    }
                }
            }
            return new DiscoveryResult(null, null);
        }

        /// <summary>
        /// 一回切ったノーツの当たり判定が消失するバグの修正（ScoreSaberさん、許して…）
        /// </summary>
        /// <param name="noteController"></param>
        /// <param name="__instance"></param>
        /// <param name="__runOriginal"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPriority(255)]
        public static bool AddNoteDataPrefix(ref NoteController noteController, object __instance, ref bool __runOriginal)
        {
            if (s_fieldInfo == null) {
                s_fieldInfo = __instance.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(x => x.FieldType.Equals(typeof(Dictionary<NoteData, NoteCutInfo>)));
            }
            var dic = (Dictionary<NoteData, NoteCutInfo>)s_fieldInfo?.GetValue(__instance);
            __runOriginal = dic == null || !dic.ContainsKey(noteController.noteData);
            return __runOriginal;
        }
    }
}
