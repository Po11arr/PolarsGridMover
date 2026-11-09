using System;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;

namespace PolarsGridMover;

// Messages for the player, drawn just above the game's notification list with the same
// font, size and dark backdrop.
//
// Vanilla notifications share one pool of 80 text buffers between every notification of
// the game and of every mod and plugin. When it runs dry a new notification gets no text
// and is silently not drawn, which made messages appear only some of the time. Drawing
// the text here needs no buffer from that pool. A postfix on MyHudNotifications.Draw runs
// in the HUD's own draw pass, so the text hides with the HUD like vanilla notifications.
// If the patch can't be applied, messages fall back to a vanilla notification.
internal static class HudText
{
    private const string Font = "White";

    // Each message is a new object swapped in as a whole, since Show runs on the update
    // thread and the HUD draws on another. Size is only touched by the draw.
    private sealed class Message
    {
        public readonly string Text;
        public readonly int Until;
        public Vector2 Size;

        public Message(string text, int until)
        {
            Text = text;
            Until = until;
        }
    }

    private static Message current;
    private static MyHudNotification fallback;

    // Cleared by the draw if drawing fails, read by Show
    private static volatile bool available;

    internal static bool Available
    {
        get => available;
        private set => available = value;
    }

    internal static void Init(Harmony harmony)
    {
        try
        {
            var draw = AccessTools.Method(typeof(MyHudNotifications), nameof(MyHudNotifications.Draw), Type.EmptyTypes);
            if (draw == null)
            {
                Plugin.Log("Own message drawing is off: this game version's HUD is different");
                return;
            }
            harmony.Patch(draw, postfix: new HarmonyMethod(typeof(HudText), nameof(DrawPostfix)));
            Available = true;
        }
        catch (Exception e)
        {
            Plugin.Log("Own message drawing is off, patching failed: " + e);
            Available = false;
        }
    }

    internal static void Dispose()
    {
        Clear();
        Available = false;
    }

    internal static void Show(string text, int ms)
    {
        if (Available)
        {
            Volatile.Write(ref current, new Message(text, Environment.TickCount + ms));
            return;
        }

        var notifications = MyHud.Notifications;
        if (notifications == null)
            return;
        if (fallback != null)
            notifications.Remove(fallback);
        fallback = new MyHudNotification(MyCommonTexts.CustomText, ms, Font, priority: 1, level: MyNotificationLevel.Important) { HasFog = true };
        fallback.SetTextFormatArguments(text);
        notifications.Add(fallback);
    }

    internal static void Clear()
    {
        Volatile.Write(ref current, null);
        if (fallback != null)
            MyHud.Notifications?.Remove(fallback);
        fallback = null;
    }

    // Harmony postfix on MyHudNotifications.Draw()
    private static void DrawPostfix(MyHudNotifications __instance)
    {
        var message = Volatile.Read(ref current);
        if (message == null)
            return;

        try
        {
            if (unchecked(Environment.TickCount - message.Until) >= 0)
            {
                // Only if no newer message arrived in the meantime
                Interlocked.CompareExchange(ref current, null, message);
                return;
            }

            var scale = MyGuiSandbox.GetDefaultTextScaleWithLanguage() * 1.2f;
            if (message.Size == Vector2.Zero)
                message.Size = MyGuiManager.MeasureString(Font, message.Text, scale);

            var size = message.Size;
            // One and a half lines above the first vanilla notification. The backdrop is
            // placed the way vanilla places it, centred on the top of the text.
            var top = new Vector2(__instance.Position.X, __instance.Position.Y - size.Y * 1.5f);
            var fog = top;
            MyGuiTextShadows.DrawShadow(ref fog, ref size);
            MyGuiManager.DrawString(Font, message.Text, top, scale, Color.White, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP);
        }
        catch (Exception e)
        {
            // Never break the HUD: fall back to vanilla notifications from here on
            Interlocked.CompareExchange(ref current, null, message);
            Available = false;
            Plugin.Log("Drawing a message failed, using game notifications instead: " + e);
        }
    }
}
