using System.ComponentModel;
using System.Runtime.CompilerServices;
using CampusNetLogin.Models;

namespace CampusNetLogin.ViewModels;

/// <summary>
/// 备用网络的可绑定包装，供界面列表使用。
/// 界面上的勾选状态需要即时反映到配置模型，因此这里做双向同步。
/// </summary>
public sealed class BackupNetworkItem : INotifyPropertyChanged
{
    private readonly BackupNetwork _model;

    public BackupNetworkItem(BackupNetwork model)
    {
        _model = model;
    }

    public BackupNetwork Model => _model;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Ssid
    {
        get => _model.Ssid;
        set
        {
            if (_model.Ssid == value) return;
            _model.Ssid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceText));
        }
    }

    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            OnPropertyChanged();
        }
    }

    public BackupSource Source
    {
        get => _model.Source;
        set
        {
            if (_model.Source == value) return;
            _model.Source = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceText));
            OnPropertyChanged(nameof(IsManual));
            OnPropertyChanged(nameof(IsSystem));
        }
    }

    /// <summary>来源的展示文本。</summary>
    public string SourceText => Source == BackupSource.System ? "系统已保存" : "手动添加";

    public bool IsManual => Source == BackupSource.Manual;
    public bool IsSystem => Source == BackupSource.System;

    /// <summary>密码的加密存储值（仅手动添加的有意义）。</summary>
    public string EncryptedPassword
    {
        get => _model.Password;
        set
        {
            if (_model.Password == value) return;
            _model.Password = value;
            OnPropertyChanged();
        }
    }

    /// <summary>列表里显示的说明文字（含信号提示）。</summary>
    private string _hint = string.Empty;
    public string Hint
    {
        get => _hint;
        set
        {
            if (_hint == value) return;
            _hint = value;
            OnPropertyChanged();
        }
    }
}
