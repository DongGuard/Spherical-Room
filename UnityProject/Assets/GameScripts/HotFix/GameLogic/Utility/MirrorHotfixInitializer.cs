using System;
using System.Collections.Generic;
using System.Reflection;
using TEngine;
using UnityEngine;

namespace GameLogic
{
    /// <summary>
    /// 补跑 Mirror Weaver 为热更程序集生成的初始化代码。
    /// <para>
    /// Weaver 会在每个用到 Mirror 的程序集里生成 <c>Mirror.GeneratedNetworkCode.InitReadWriters</c>，
    /// 通过 [RuntimeInitializeOnLoadMethod] 注册自定义类型（如 PlayerInputState）的读写器。
    /// Unity 只在启动时扫描 AOT 程序集，HybridCLR 运行时 Assembly.Load 的热更 DLL 不会被扫描，
    /// 因此需要在热更入口手动调用一次，否则 Command 参数无法序列化。
    /// </para>
    /// Command / ClientRpc 由 NetworkBehaviour 子类的静态构造函数注册，首次使用时自动执行，不受影响。
    /// </summary>
    public static class MirrorHotfixInitializer
    {
        private const string GeneratedTypeName = "Mirror.GeneratedNetworkCode";

        public static void Initialize(IEnumerable<Assembly> hotfixAssemblies)
        {
#if !UNITY_EDITOR
            // 编辑器下热更程序集由 Unity 正常加载，这些方法已经自动执行过，不重复调用
            if (hotfixAssemblies == null)
            {
                return;
            }

            foreach (Assembly assembly in hotfixAssemblies)
            {
                InvokeGeneratedInitializers(assembly);
            }
#endif
        }

        private static void InvokeGeneratedInitializers(Assembly assembly)
        {
            Type type = assembly.GetType(GeneratedTypeName, false);
            if (type == null)
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo method in type.GetMethods(flags))
            {
                if (method.GetParameters().Length != 0 ||
                    method.GetCustomAttribute<RuntimeInitializeOnLoadMethodAttribute>() == null)
                {
                    continue;
                }

                try
                {
                    method.Invoke(null, null);
                    Log.Info($"[Mirror] 已初始化 {assembly.GetName().Name}: {type.FullName}.{method.Name}");
                }
                catch (Exception e)
                {
                    Log.Error($"[Mirror] 初始化失败 {type.FullName}.{method.Name}: {e.InnerException ?? e}");
                }
            }
        }
    }
}
