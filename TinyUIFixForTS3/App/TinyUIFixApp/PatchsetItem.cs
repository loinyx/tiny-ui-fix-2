using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TinyUIFixApp;

public sealed partial class PatchsetItem : INotifyPropertyChanged
{
    bool _enabled;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Details { get; init; }
    public string? Recommendation { get; init; }
    public bool Fixed { get; init; }

    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; Changed(); } }
    }

    public Microsoft.UI.Xaml.Visibility ToggleVisibility => Fixed ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string Pill => Fixed ? L.T("patch.required") : Recommendation is not null ? L.T("patch.recommended") : "v" + Version;
    public string Reason => Recommendation is null ? "" : L.F("patch.detected", Translate(Recommendation));
    public Microsoft.UI.Xaml.Visibility ReasonVisibility => Recommendation is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    static string Translate(string reason) => reason switch
    {
        "Because vanilla core DLLs are active." => L.T("reason.vanilla"),
        "Because NRaas mods are active." => L.T("reason.nraas"),
        _ => reason,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
