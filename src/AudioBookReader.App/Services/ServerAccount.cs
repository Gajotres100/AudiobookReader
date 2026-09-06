namespace AudioBookReader.App.Services;

/// <summary>
/// Remembers which server the app talks to, and holds the key to it.
///
/// Two different kinds of secret, kept in two different places on purpose.
///
/// The address is not a secret and lives in ordinary preferences. The token is, and lives in
/// <see cref="SecureStorage"/> — on Android that is encrypted shared preferences with the key held
/// in the platform keystore, so it is not readable by pulling the app's files off the device.
///
/// The password is stored nowhere at all. It is exchanged for a token once, at sign-in, and then
/// forgotten: a token can be revoked from the server's own settings page, and a password taken from
/// a phone is the same password as everywhere else.
/// </summary>
public class ServerAccount
{
    private const string UrlKey = "server.url";
    private const string TokenKey = "server.token";
    private const string RefreshKey = "server.refresh";

    /// <summary>Raised when the app connects or disconnects, so screens can show the change.</summary>
    public event EventHandler? Changed;

    public string? Url
    {
        get => Preferences.Default.Get<string?>(UrlKey, null);
        private set
        {
            if (value is null) Preferences.Default.Remove(UrlKey);
            else Preferences.Default.Set(UrlKey, value);
        }
    }

    public bool IsConfigured => !string.IsNullOrEmpty(Url);

    /// <summary>
    /// The stored token, or null.
    ///
    /// Secure storage reaches the keystore, which can fail on a device whose keys were invalidated
    /// by a lock-screen change or a restore from backup. That is not worth crashing over — the
    /// answer is the same as having no token, which is to ask the user to sign in again.
    /// </summary>
    public async Task<(string? Token, string? Refresh)> GetTokensAsync()
    {
        try
        {
            return (await SecureStorage.Default.GetAsync(TokenKey),
                    await SecureStorage.Default.GetAsync(RefreshKey));
        }
        catch (Exception ex)
        {
            AppLog.Info($"server tokens unreadable ({ex.GetType().Name}); treating them as absent");
            return (null, null);
        }
    }

    /// <summary>
    /// Keeps a freshly issued pair.
    ///
    /// Called far more often than at sign-in: the access token lasts about an hour and the refresh
    /// that replaces it rotates the refresh token too, so a pair stored once and never updated is
    /// only slightly better than none.
    /// </summary>
    public async Task SaveTokensAsync(string token, string? refresh)
    {
        try
        {
            await SecureStorage.Default.SetAsync(TokenKey, token);

            if (refresh is { Length: > 0 }) await SecureStorage.Default.SetAsync(RefreshKey, refresh);
        }
        catch (Exception ex)
        {
            // A keystore that will not write is a signed-in session that lasts until the app is
            // closed. Worth saying, not worth failing over.
            AppLog.Info($"server tokens not stored ({ex.GetType().Name})");
        }
    }

    public async Task SaveAsync(string url, string token, string? refresh)
    {
        Url = url;
        await SaveTokensAsync(token, refresh);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the server entirely. The token on the server itself is untouched.</summary>
    public void Forget()
    {
        Url = null;
        SecureStorage.Default.Remove(TokenKey);
        SecureStorage.Default.Remove(RefreshKey);

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
