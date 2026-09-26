namespace AudioBookReader.App.Services;

/// <summary>
/// Remembers one server the app talks to, and holds the key to it.
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
///
/// One of these per server. The first — and, before several could be kept, the only — one uses the
/// original key names, so a server set up before that change carries on signed in as it was.
/// </summary>
public class ServerAccount(string id)
{
    /// <summary>The server that was the only one, kept under the key names it always had.</summary>
    public const string LegacyId = "default";

    public string Id { get; } = id;

    private string Key(string name) => Id == LegacyId ? $"server.{name}" : $"server.{Id}.{name}";

    private string UrlKey => Key("url");
    private string NameKey => Key("name");
    private string TokenKey => Key("token");
    private string RefreshKey => Key("refresh");
    private string UserKey => Key("username");
    private string PasswordKey => Key("password");
    private string TrustAnyCertificateKey => Key("trustAnyCertificate");

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

    /// <summary>Raised when this server is connected or forgotten, so screens can show the change.</summary>
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

    /// <summary>What the user called it, when they called it anything.</summary>
    public string? Name
    {
        get => Preferences.Default.Get<string?>(NameKey, null);
        set
        {
            if (string.IsNullOrWhiteSpace(value)) Preferences.Default.Remove(NameKey);
            else Preferences.Default.Set(NameKey, value.Trim());
        }
    }

    /// <summary>
    /// How the server is shown: its given name, or else the host from its address — which is
    /// usually enough to tell a home server from a friend's.
    /// </summary>
    public string DisplayName =>
        Name
        ?? (Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url)
        ?? "";

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
            AppLog.Info($"server {Id}: tokens unreadable ({ex.GetType().Name}); treating them as absent");
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
            AppLog.Info($"server {Id}: tokens not stored ({ex.GetType().Name})");
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
            AppLog.Info($"server {Id}: sign-in not stored ({ex.GetType().Name})");
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
            AppLog.Info($"server {Id}: sign-in unreadable ({ex.GetType().Name}); treating it as absent");
            return (null, null);
        }
    }

    /// <summary>Whether a sign-in is stored, without reading it.</summary>
    public async Task<bool> RemembersSignInAsync() => (await GetSignInAsync()).Password is { Length: > 0 };

    /// <summary>Forgets the server entirely. The token on the server itself is untouched.</summary>
    public void Forget()
    {
        Url = null;
        Name = null;
        Preferences.Default.Remove(TrustAnyCertificateKey);
        SecureStorage.Default.Remove(TokenKey);
        SecureStorage.Default.Remove(RefreshKey);
        ForgetSignIn();

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
