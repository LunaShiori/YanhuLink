using Android.App;
using Android.Content;

namespace YanhuLink.Droid;

/// <summary>
/// 开机自启接收器。
///
/// 开机后如果用户开了「自动认证」，就把前台服务拉起来。
/// 若不满足条件（没配账号 / 关了开关），什么都不做 ——
/// 不申请无用唤醒，也不后台偷跑。
///
/// 注意：Android 10+ 对后台启动前台服务有限制，
/// 但 BOOT_COMPLETED 属于明确允许的例外场景之一。
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter(new[]
{
    Intent.ActionBootCompleted,
    "android.intent.action.QUICKBOOT_POWERON",
    Intent.ActionMyPackageReplaced,
})]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;

        try
        {
            var cfg = ConfigStore.Load();
            if (!cfg.AutoLoginOnWifi) return;
            if (string.IsNullOrWhiteSpace(cfg.Username)) return;

            WifiLoginService.Start(context);
        }
        catch
        {
            // 开机流程里绝不能抛异常
        }
    }
}
