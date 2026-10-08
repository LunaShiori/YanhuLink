using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace YanhuLink.Droid;

/// <summary>
/// 首次设置向导。
///
/// 职责：
///   1. 讲清楚「程序做什么」和「为什么要两个权限」
///   2. 一次性申请 位置 + 通知 权限（Android 13+ 通知是运行时权限）
///   3. 完成后跳到主界面继续填账号
///
/// 设计取舍：
///   没有做成强行拦截 —— 用户点「跳过」直接进主界面，
///   只是 WiFi 自动认证会因缺权限而效果打折，界面会提示去授权。
///   拦死用户是新手引导最常见的坏味道。
/// </summary>
[Activity(
    Label = "砚湖连",
    Theme = "@style/AppTheme",
    Exported = false,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class OnboardingActivity : Activity
{
    /// <summary>权限请求码 —— 两个权限合并成一次请求。</summary>
    private const int PermRequestCode = 2001;

    private bool _requesting;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_onboarding);

        var btnGrant = FindViewById<Google.Android.Material.Button.MaterialButton>(
            Resource.Id.btnGrantPermissions);
        var btnSkip = FindViewById<Google.Android.Material.Button.MaterialButton>(
            Resource.Id.btnSkip);

        btnGrant!.Click += (_, _) => RequestPermissionsThenFinish();
        btnSkip!.Click += (_, _) => FinishOnboarding();

        // 已有权限的话，按钮文案改成「开始使用」，避免重复申请
        if (HasAllPermissions())
            btnGrant.Text = "开始使用";
    }

    // ---------------------------------------------------------------------
    // 权限
    // ---------------------------------------------------------------------

    private List<string> MissingPermissions()
    {
        var missing = new List<string>();

        // 位置权限：读 WiFi 名称的必要条件
        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.AccessFineLocation)
            != Permission.Granted)
        {
            missing.Add(Manifest.Permission.AccessFineLocation);
        }

        // 通知权限：Android 13+ 才是运行时权限
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications)
                != Permission.Granted)
            {
                missing.Add(Manifest.Permission.PostNotifications);
            }
        }

        return missing;
    }

    private bool HasAllPermissions() => MissingPermissions().Count == 0;

    private void RequestPermissionsThenFinish()
    {
        if (_requesting) return;

        var missing = MissingPermissions();
        if (missing.Count == 0)
        {
            FinishOnboarding();
            return;
        }

        _requesting = true;
        ActivityCompat.RequestPermissions(this, missing.ToArray(), PermRequestCode);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _requesting = false;

        if (requestCode != PermRequestCode) return;

        // 无论同意还是拒绝，都继续 —— 拒绝了就在主界面提示去设置里开
        bool allGranted = grantResults.Length > 0;
        foreach (var g in grantResults)
        {
            if (g != Permission.Granted) { allGranted = false; break; }
        }

        if (!allGranted)
        {
            // 用简短提示告知后果，不弹模态框打断流程
            Toast.MakeText(this,
                "部分权限未授予，WiFi 名称可能显示为「未知」，可在系统设置中补充授权",
                ToastLength.Long)?.Show();
        }

        FinishOnboarding();
    }

    // ---------------------------------------------------------------------
    // 结束向导
    // ---------------------------------------------------------------------

    private void FinishOnboarding()
    {
        try
        {
            var cfg = ConfigStore.Load();
            cfg.OnboardingDone = true;
            ConfigStore.Save(cfg);
        }
        catch { /* ignore */ }

        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        StartActivity(intent);
        Finish();
    }
}
