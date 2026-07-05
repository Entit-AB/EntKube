namespace EntKube.Web.Services;

/// <summary>
/// Circuit-scoped in-app notification channel. Any component can raise a toast
/// (success / error / info / warning) and the single <c>ToastHost</c> in the
/// layout renders and auto-dismisses them. Replaces the per-page inline
/// <c>error</c>/<c>savedMessage</c> strings so feedback is visible regardless of
/// which panel triggered it.
/// </summary>
public class ToastService
{
    private readonly List<ToastMessage> _toasts = [];

    public IReadOnlyList<ToastMessage> Toasts => _toasts;

    /// <summary>Raised whenever the toast list changes so the host can re-render.</summary>
    public event Action? OnChange;

    public void Success(string message, string? title = null, int autoDismissMs = 4000)
        => Show(ToastLevel.Success, message, title, autoDismissMs);

    public void Info(string message, string? title = null, int autoDismissMs = 4000)
        => Show(ToastLevel.Info, message, title, autoDismissMs);

    public void Warning(string message, string? title = null, int autoDismissMs = 6000)
        => Show(ToastLevel.Warning, message, title, autoDismissMs);

    /// <summary>Errors do not auto-dismiss by default so the user can read them.</summary>
    public void Error(string message, string? title = null, int autoDismissMs = 0)
        => Show(ToastLevel.Error, message, title, autoDismissMs);

    public void Show(ToastLevel level, string message, string? title = null, int autoDismissMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        ToastMessage toast = new(Guid.NewGuid(), level, message.Trim(), title);
        _toasts.Add(toast);
        OnChange?.Invoke();

        if (autoDismissMs > 0)
        {
            _ = DismissAfterAsync(toast.Id, autoDismissMs);
        }
    }

    public void Dismiss(Guid id)
    {
        int removed = _toasts.RemoveAll(t => t.Id == id);
        if (removed > 0)
            OnChange?.Invoke();
    }

    private async Task DismissAfterAsync(Guid id, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        Dismiss(id);
    }
}

public enum ToastLevel
{
    Success,
    Info,
    Warning,
    Error,
}

public record ToastMessage(Guid Id, ToastLevel Level, string Message, string? Title);
