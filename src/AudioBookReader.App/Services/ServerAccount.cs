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
/// The password is kept only if asked for, and then in the same protected place. It is not needed
/// to stay signed in — the refresh token does that — but that token dies after thirty unused days,
/// and a book app can easily go a month unopened. Without the password that means typing everything
/// again; with it the app quietly signs itself back in. The choice belongs to whoever owns the
/// phone, so it is a switch rather than a decision made here.
/// </summary>
public class ServerAccount
{
    private const string UrlKey = "server.url";
    private const string TokenKey = "server.token";
    private const string RefreshKey = "server.refresh";
    private const string UserKey = "server.username";
    private const string PasswordKey = "server.password";
    private const string TrustAnyCertificateKey = "server.trustAnyCertificate";
    private const string OpenOnStartKey = "server.openOnStart";

    /// <summary>
    /// Land on the server shelf instead of the local library when the app starts.
    ///
    /// Only meaningful once a server is configured — offering it earlier would be a switch for a
    /// screen the user cannot reach yet. Checked once, at the app's own startup, rather than on
    /// every rebuild of the shell a language change causes: the point is "when I open the app", not
    /// "whenever the shell happens to be rebuilt".
    /// </summary>
    public bool OpenServerOnStart
    {
        get => Preferences.Default.Get(OpenOnStartKey, false);
        set => Preferences.Default.Set(OpenOnStartKey, value);
    }

    /// <summary>
    /// Skip certificate validation for this connection — for a self-signed server with no other way
    /// in.
    ///
    /// Off by default and only ever on because the person who owns the phone typed this exact
    /// address themselves and turned this on knowingly for it. That is a materially smaller risk
    /// than trusting every certificate everywhere: someone intercepting traffic still has to be
    /// sitting on the path to the one address the user already chose to trust, not merely on any
    /// network the phone happens to join.
    /// </summary>
    public bool TrustAnyCertificate
    {
        get => Preferences.Default.Get(TrustAnyCertificateKey, false);
        set => Preferences.Default.Set(TrustAnyCertificateKey, value);
    }

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

    /// <summary>
    /// Keeps the sign-in itself, so a session that has expired beyond recovery can be renewed
    /// without asking. Only ever called when the user has asked for it.
    /// </summary>
    public async Task RememberSignInAsync(string username, string password)
    {
        try
        {
            await SecureStorage.Default.SetAsync(UserKey, username);
            await SecureStorage.Default.SetAsync(PasswordKey, password);
        }
        catch (Exception ex)
        {
            AppLog.Info($"server sign-in not stored ({ex.GetType().Name})");
        }
    }

    public void ForgetSignIn()
    {
        SecureStorage.Default.Remove(UserKey);
        SecureStorage.Default.Remove(PasswordKey);
    }

    /// <summary>The stored sign-in, or nulls when there is none to use.</summary>
    public async Task<(string? Username, string? Password)> GetSignInAsync()
    {
        try
        {
            return (await SecureStorage.Default.GetAsync(UserKey),
                    await SecureStorage.Default.GetAsync(PasswordKey));
        }
        catch (Exception ex)
        {
            AppLog.Info($"server sign-in unreadable ({ex.GetType().Name}); treating it as absent");
            return (null, null);
        }
    }

    /// <summary>Whether a sign-in is stored, without reading it.</summary>
    public async Task<bool> RemembersSignInAsync() => (await GetSignInAsync()).Password is { Length: > 0 };

    /// <summary>Forgets the server entirely. The token on the server itself is untouched.</summary>
    public void Forget()
    {
        Url = null;
        SecureStorage.Default.Remove(TokenKey);
        SecureStorage.Default.Remove(RefreshKey);
        ForgetSignIn();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
