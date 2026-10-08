using Android;
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using AndroidX.Core.Content;
using Google.Android.Material.Button;
using Google.Android.Material.Snackbar;

namespace YanhuLink.Android;

/// <summary>
/// 主界面。
///
/// 只做四件事：
///   1. 展示当前状态（网络 / 认证结果）
///   2. 让用户填账号密码
///   3. 开关自动化选项
///   4. 触发「立即登录」与「保存」
///
/// 界面逻辑刻意保持简单 —— 没有 MVVM、没有数据绑定，
/// 手机端一个页面用命令式代码反而更直观、更好排错。
/// </summary>
[Activity(
    Label = "砚湖连",
    Theme = "@style/AppTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private AndroidConfig _cfg = new();
    private bool _loading;      // 回填控件时抑制事件，避免误保存

    private View? _dotStatus;
    private TextView? _txtStatusTitle;
    private TextView? _txtStatusDetail;
    private TextView? _txtNetwork;

    private Spinner? _spinnerIsp;
    private EditText? _editUsername;
    private EditText? _editPassword;

    private SwitchCompat? _switchOnOpen;
    private SwitchCompat? _switchOnWifi;
    private SwitchCompat? _switchBoot;
    private EditText? _editSsids;

    private MaterialButton? _btnLoginNow;
    private MaterialButton? _btnSave;

    /// <summary>运营商列表，顺序与 AndroidConfig.IspSuffixes 对应。</summary>
    private static readonly string[] IspNames =
        { "校园网", "中国移动", "中国电信", "中国联通" };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _cfg = ConfigStore.Load();

        // 第一次打开 → 先进向导
        if (!_cfg.OnboardingDone)
        {
            StartActivity(new Android.Content.Intent(this, typeof(OnboardingActivity)));
            Finish();
            return;
        }

        SetContentView(Resource.Layout.activity_main);
        BindViews();
        FillUiFromConfig();
        WireEvents();
        RefreshStatus();
    }

    protected override void OnResume()
    {
        base.OnResume();
        // 从系统设置返回时权限可能变了，或服务状态有更新
        WifiLoginService.StatusChanged -= OnServiceStatusChanged;
        WifiLoginService.StatusChanged += OnServiceStatusChanged;
        RefreshStatus();

        // 「打开应用时自动登录」
        if (_cfg.AutoLoginOnOpen)
        {
            // 通过前台服务触发，复用同一套逻辑与结果反馈
            WifiLoginService.Start(this);
            WifiLoginService.LoginNow(this);
        }
        else if (_cfg.AutoLoginOnWifi)
        {
            // 至少要保证后台监听在跑
            WifiLoginService.Start(this);
        }
    }

    protected override void OnPause()
    {
        WifiLoginService.StatusChanged -= OnServiceStatusChanged;
        base.OnPause();
    }

    // ---------------------------------------------------------------------
    // 视图绑定
    // ---------------------------------------------------------------------

    private void BindViews()
    {
        _dotStatus = FindViewById<View>(Resource.Id.dotStatus);
        _txtStatusTitle = FindViewById<TextView>(Resource.Id.txtStatusTitle);
        _txtStatusDetail = FindViewById<TextView>(Resource.Id.txtStatusDetail);
        _txtNetwork = FindViewById<TextView>(Resource.Id.txtNetwork);

        _spinnerIsp = FindViewById<Spinner>(Resource.Id.spinnerIsp);
        _editUsername = FindViewById<EditText>(Resource.Id.editUsername);
        _editPassword = FindViewById<EditText>(Resource.Id.editPassword);

        _switchOnOpen = FindViewById<SwitchCompat>(Resource.Id.switchOnOpen);
        _switchOnWifi = FindViewById<SwitchCompat>(Resource.Id.switchOnWifi);
        _switchBoot = FindViewById<SwitchCompat>(Resource.Id.switchBoot);
        _editSsids = FindViewById<EditText>(Resource.Id.editSsids);

        _btnLoginNow = FindViewById<MaterialButton>(Resource.Id.btnLoginNow);
        _btnSave = FindViewById<MaterialButton>(Resource.Id.btnSave);

        // 运营商下拉
        var adapter = new ArrayAdapter<string>(
            this, global::Android.Resource.Layout.SimpleSpinnerItem, IspNames);
        adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _spinnerIsp!.Adapter = adapter;

        // 「开机自启」开关：本版跟随 WiFi 监听一起生效，
        // 直接禁用并说明原因，避免用户误以为它独立可控却不起作用。
        _switchBoot!.Enabled = false;
        _switchBoot.Alpha = 0.5f;
    }

    private void FillUiFromConfig()
    {
        _loading = true;
        try
        {
            int ispIndex = Array.IndexOf(IspNames, _cfg.Isp);
            _spinnerIsp!.SetSelection(ispIndex >= 0 ? ispIndex : 0);

            _editUsername!.Text = _cfg.Username;

            // 密码解密后回填（换设备后解不开则为空，界面会自然提示重输）
            var pwd = PasswordProtector.Decrypt(_cfg.PasswordEncrypted);
            _editPassword!.Text = pwd;

            _switchOnOpen!.Checked = _cfg.AutoLoginOnOpen;
            _switchOnWifi!.Checked = _cfg.AutoLoginOnWifi;
            _switchBoot!.Checked = _cfg.AutoLoginOnWifi;   // 开机自启跟随 WiFi 监听
            _editSsids!.Text = string.Join(", ", _cfg.TargetSsids);
        }
        finally
        {
            _loading = false;
        }
    }

    private void WireEvents()
    {
        _btnLoginNow!.Click += (_, _) =>
        {
            // 先保存，再触发 —— 否则用户改了密码点登录会用旧配置
            if (!SaveFromUi(showToast: false)) return;
            if (_cfg.AutoLoginOnWifi) WifiLoginService.Start(this);

            _btnLoginNow.Enabled = false;
            _btnLoginNow.Text = "正在认证…";
            _txtStatusTitle!.Text = "正在认证…";
            SetDotColor(Resource.Color.warn_amber);

            WifiLoginService.LoginNow(this);

            // 10 秒后无论如何恢复按钮，避免中途出错卡住
            _btnLoginNow.PostDelayed(() =>
            {
                if (_btnLoginNow != null) _btnLoginNow.Enabled = true;
                if (_btnLoginNow != null) _btnLoginNow.Text = "立即登录";
                RefreshStatus();
            }, 10_000);
        };

        _btnSave!.Click += (_, _) =>
        {
            if (SaveFromUi(showToast: true))
            {
                if (_cfg.AutoLoginOnWifi) WifiLoginService.Start(this);
                else WifiLoginService.Stop(this);
                RefreshStatus();
            }
        };

        // 任一开关变化立即生效（不必等用户点保存）
        _switchOnWifi!.CheckedChange += (_, e) =>
        {
            if (_loading) return;
            if (e.IsChecked)
            {
                EnsurePermissionsThen(() =>
                {
                    WifiLoginService.Start(this);
                    RefreshStatus();
                });
            }
            else
            {
                WifiLoginService.Stop(this);
                RefreshStatus();
            }
        };

        // 「打开即登录」依赖于能拿到网络状态，没权限时同样要提示
        _switchOnOpen!.CheckedChange += (_, _) =>
        {
            if (_loading) return;
            if (e.IsChecked) EnsurePermissionsThen(RefreshStatus);
            else RefreshStatus();
        };
    }

    // ---------------------------------------------------------------------
    // 保存
    // ---------------------------------------------------------------------

    private bool SaveFromUi(bool showToast)
    {
        var username = _editUsername!.Text?.Trim() ?? string.Empty;
        var password = _editPassword!.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            Snackbar.Make(FindViewById(global::Android.Resource.Id.Content)!,
                "请先填写学号 / 上网账号", Snackbar.LengthLong).Show();
            return false;
        }

        _cfg.Isp = IspNames[Math.Max(0, _spinnerIsp!.SelectedItemPosition)];
        _cfg.Username = username;

        // 密码变了才重新加密 —— 避免每次保存都换了密文（nonce 随机）
        var oldPlain = PasswordProtector.Decrypt(_cfg.PasswordEncrypted);
        if (password != oldPlain)
            _cfg.PasswordEncrypted = PasswordProtector.Encrypt(password);

        _cfg.SavePassword = !string.IsNullOrEmpty(password);
        _cfg.AutoLoginOnOpen = _switchOnOpen!.Checked;
        _cfg.AutoLoginOnWifi = _switchOnWifi!.Checked;

        // 解析 SSID 列表
        var ssidRaw = _editSsids!.Text ?? string.Empty;
        _cfg.TargetSsids = ssidRaw
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ok = ConfigStore.Save(_cfg);

        if (showToast)
        {
            Toast.MakeText(this, ok ? "设置已保存" : "保存失败，请重试",
                ToastLength.Short)?.Show();
        }
        return ok;
    }

    // ---------------------------------------------------------------------
    // 权限辅助
    // ---------------------------------------------------------------------

    private bool HasLocationPermission() =>
        ContextCompat.CheckSelfPermission(this, Manifest.Permission.AccessFineLocation)
        == Permission.Granted;

    private bool HasNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return true;
        return ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications)
               == Permission.Granted;
    }

    /// <summary>
    /// 权限齐全则直接执行；缺权限先申请，授予后才执行。
    /// 用户拒绝时不执行动作，但给出明确提示。
    /// </summary>
    private void EnsurePermissionsThen(Action onReady)
    {
        var missing = new List<string>();
        if (!HasLocationPermission()) missing.Add(Manifest.Permission.AccessFineLocation);
        if (!HasNotificationPermission()) missing.Add(Manifest.Permission.PostNotifications);

        if (missing.Count == 0)
        {
            onReady();
            return;
        }

        Snackbar.Make(FindViewById(global::Android.Resource.Id.Content)!,
            "需要授予权限才能后台自动认证", Snackbar.LengthLong)
            .SetAction("去授权", _ =>
            {
                var intent = new Android.Content.Intent(this, typeof(OnboardingActivity));
                StartActivity(intent);
            })
            .Show();

        onReady();   // 仍然放行 —— 前台服务能起来，只是 SSID 可能读不到
    }

    // ---------------------------------------------------------------------
    // 状态刷新
    // ---------------------------------------------------------------------

    private void OnServiceStatusChanged()
        => RunOnUiThread(new Action(RefreshStatus));

    private void RefreshStatus()
    {
        try
        {
            var ssid = WifiHelper.GetCurrentSsid();
            var wifiOn = WifiHelper.IsWifiConnected();

            // 当前网络行
            if (_txtNetwork != null)
            {
                if (!wifiOn)
                    _txtNetwork.Text = "当前网络：未连接 WiFi";
                else if (string.IsNullOrEmpty(ssid))
                    _txtNetwork.Text = "当前网络：WiFi（名称不可读 · 可能缺位置权限）";
                else
                    _txtNetwork.Text = $"当前网络：{ssid}";
            }

            // 状态卡片
            var outcome = WifiLoginService.LastOutcome;
            if (outcome == null)
            {
                SetStatus("准备就绪",
                    _cfg.AutoLoginOnWifi
                        ? "后台监听已开启，连上校园网后会自动认证"
                        : "后台监听已关闭，可点「立即登录」手动认证",
                    wifiOn && WifiHelper.MatchesTarget(ssid, _cfg.TargetSsids)
                        ? Resource.Color.lake_accent
                        : Resource.Color.text_secondary);
            }
            else if (outcome.Success)
            {
                SetStatus(outcome.AlreadyOnline ? "已在线" : "登录成功",
                    $"{outcome.Message} · {outcome.Time:HH:mm:ss}",
                    Resource.Color.ok_green);
            }
            else
            {
                SetStatus("认证未成功", outcome.Message, Resource.Color.err_red);
            }

            // 自动登录按钮的可用性：没账号时提示
            if (_btnLoginNow != null)
            {
                bool hasAccount = !string.IsNullOrWhiteSpace(_cfg.Username) &&
                                  !string.IsNullOrEmpty(
                                      PasswordProtector.Decrypt(_cfg.PasswordEncrypted));
                _btnLoginNow.Enabled = hasAccount;
            }
        }
        catch { /* 状态刷新失败不影响应用运行 */ }
    }

    private void SetStatus(string title, string detail, int colorRes)
    {
        if (_txtStatusTitle != null) _txtStatusTitle.Text = title;
        if (_txtStatusDetail != null) _txtStatusDetail.Text = detail;
        SetDotColor(colorRes);
    }

    private void SetDotColor(int colorRes)
    {
        try
        {
            var bg = _dotStatus?.Background;
            if (bg != null)
            {
                var color = new Color(ContextCompat.GetColor(this, colorRes));
                bg.SetColorFilter(color, PorterDuff.Mode.SrcIn!);
            }
        }
        catch { /* ignore */ }
    }
}
