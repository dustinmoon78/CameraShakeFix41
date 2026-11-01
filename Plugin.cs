// SPDX-License-Identifier: AGPL-3.0-or-later
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace CameraShakeFix41
{
    /// <summary>
    /// 受击不抖（自研件）——只挡「被命中」。
    ///
    /// 抖动的唯一总出口是 ForceEffector.AddForce(Vector3, float, float, float)（3 参重载内部转调它）。
    /// 但直接缩放它会连其他来源的晃动一起变静（例如 DoorDash 冲刺撞门调的正是这条链）。
    /// 所以本件在「受击入口」上下一道闸：只有当前调用栈确实来自被命中时，才缩放 strength。
    ///
    /// 受击入口（4.1.6 反编译 + 参考程序集双证）：
    ///   EffectsController.OnPlayerDamaged(float, EBodyPart, EDamageType, float, MaterialType)
    ///       ← 订阅 EFT.Player.OnDamageReceived（Player.cs:25808 触发），子弹/爆炸等一切伤害走这里
    ///   EFT.Player.ShotReactions(DamageInfo, EBodyPart)
    ///       ← 被击中时的躯干/头部反应；其中「被近战武器砍中」分支才会调 AddForce（Player.cs:26110）
    ///
    /// 闸门标记带帧号复核：万一受击方法抛异常导致 Postfix 没跑，标记也在下一帧自动失效，
    /// 不会污染后续无关的 AddForce 调用。
    /// </summary>
    [BepInPlugin(Guid, "中弹稳镜 HitSteady", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.dsh.camerashakefix";

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> Multiplier;

        private static bool _inDamageReaction;
        private static int _damageFrame = -1;

        private Harmony _harmony;

        private void Awake()
        {
            Enabled = Config.Bind("受击抖动", "启用", true,
                "开启后，被命中时的镜头与手部抖动会乘以下面的倍率。");

            Multiplier = Config.Bind("受击抖动", "抖动倍率", 0f,
                new ConfigDescription(
                    "0 = 被命中完全不抖；1 = 原版。\\n" +
                    "只作用于「被命中」这一条路径（子弹/爆炸命中、被近战砍中），撞门等其他晃动不受影响。",
                    new AcceptableValueRange<float>(0f, 1f)));

            _harmony = new Harmony(Guid);

            int gates = 0;
            if (PatchDamageGate(typeof(EffectsController), "OnPlayerDamaged", 5)) gates++;
            if (PatchDamageGate(typeof(EFT.Player), "ShotReactions", 2)) gates++;
            bool addForceOk = PatchAddForce();

            Logger.LogWarning(string.Format(
                "中弹稳镜 HitSteady 已装载：受击入口 {0}/2，AddForce 锚点 {1}（倍率 {2}）",
                gates, addForceOk ? "在位" : "缺失", Multiplier.Value));

            if (gates == 0 || !addForceOk)
            {
                Logger.LogError("中弹稳镜 HitSteady：锚点不全，本件不生效（游戏内表现等同原版）。");
            }
        }

        /// <summary>给一个「受击入口」方法挂上闸门（进入置位、正常返回复位）。</summary>
        private bool PatchDamageGate(Type type, string methodName, int paramCount)
        {
            try
            {
                var target = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == paramCount);

                if (target == null)
                {
                    Logger.LogError(string.Format("找不到受击入口 {0}.{1}（{2} 参），该入口不设闸。", type.Name, methodName, paramCount));
                    return false;
                }

                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(EnterDamageReaction), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ExitDamageReaction), BindingFlags.Static | BindingFlags.NonPublic)));

                Logger.LogWarning("受击闸门已挂：" + target.FullDescription());
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(string.Format("给 {0}.{1} 挂受击闸门失败：{2}", type.Name, methodName, ex));
                return false;
            }
        }

        /// <summary>挂抖动缩放点。</summary>
        private bool PatchAddForce()
        {
            try
            {
                var target = typeof(ForceEffector)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AddForce" && m.GetParameters().Length == 4);

                if (target == null)
                {
                    Logger.LogError("找不到 ForceEffector.AddForce(Vector3,float,float,float)，本件不生效。");
                    return false;
                }

                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(ForceEffectorAddForcePrefix), BindingFlags.Static | BindingFlags.NonPublic)));

                Logger.LogWarning("抖动缩放点已挂：" + target.FullDescription());
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("挂抖动缩放点失败：" + ex);
                return false;
            }
        }

        private static void EnterDamageReaction()
        {
            _inDamageReaction = true;
            _damageFrame = Time.frameCount;
        }

        private static void ExitDamageReaction()
        {
            _inDamageReaction = false;
        }

        /// <summary>
        /// 位序取第 2 个形参（= strength）；不用具名参数：混淆过的程序集里形参名可能失效。
        /// 只有「正处在受击调用栈里且是同一帧」才缩放 —— 其他来源的晃动原样放行。
        /// </summary>
        private static void ForceEffectorAddForcePrefix(ref float __1)
        {
            if (!Enabled.Value || !_inDamageReaction)
            {
                return;
            }

            if (_damageFrame != Time.frameCount)
            {
                // 陈旧标记（受击方法异常退出导致 Postfix 没跑）——丢弃，不污染本次调用。
                _inDamageReaction = false;
                return;
            }

            __1 *= Multiplier.Value;
        }
    }
}
