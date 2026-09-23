using System.Globalization;
using System.Resources;

namespace AudioBookReader.Core;

/// <summary>
/// The messages this library raises, in whatever language is current.
///
/// Core knows nothing about the app around it, but the exceptions it throws end up in front of a
/// reader, so they have to translate the same way the screens do. Its own resource set, because a
/// library carrying the whole app's vocabulary would be the wrong shape.
///
/// The culture is whatever the process has set: the app sets it at startup and again on every
/// change of language.
/// </summary>
public static class CoreStrings
{
    private static readonly ResourceManager Resources =
        new("AudioBookReader.Core.Resources.CoreResources", typeof(CoreStrings).Assembly);

    private static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>{0} is a file name.</summary>
    public static string Audio_UnreadableTags => Get(nameof(Audio_UnreadableTags));

    /// <summary>I can’t read this file as a book. EPUB and plain text are supported.</summary>
    public static string Book_UnknownFormat => Get(nameof(Book_UnknownFormat));

    /// <summary>The sign-in went through, but the server returned no token.</summary>
    public static string Server_NoToken => Get(nameof(Server_NoToken));

    /// <summary>No server address has been set.</summary>
    public static string Server_NotConfigured => Get(nameof(Server_NotConfigured));

    /// <summary>{0} is the server address.</summary>
    public static string Server_Unreachable => Get(nameof(Server_Unreachable));

    /// <summary>{0} is the server address.</summary>
    public static string Server_NoAnswer => Get(nameof(Server_NoAnswer));

    /// <summary>The sign-in to the server is no longer valid.</summary>
    public static string Server_SignInExpired => Get(nameof(Server_SignInExpired));
    public static string Server_WrongCredentials => Get(nameof(Server_WrongCredentials));
    public static string Server_TokenRejected => Get(nameof(Server_TokenRejected));
    public static string Server_CheckAddress => Get(nameof(Server_CheckAddress));

    /// <summary>This account has no access to that on the server.</summary>
    public static string Server_Forbidden => Get(nameof(Server_Forbidden));

    /// <summary>The server does not know about that.</summary>
    public static string Server_NotFound => Get(nameof(Server_NotFound));

    /// <summary>{0} is an HTTP status code, e.g. 500.</summary>
    public static string Server_Replied => Get(nameof(Server_Replied));

    /// <summary>{0} is the server address.</summary>
    public static string Server_NotAudiobookshelf => Get(nameof(Server_NotAudiobookshelf));

    /// <summary>The server address is empty.</summary>
    public static string Server_EmptyAddress => Get(nameof(Server_EmptyAddress));

    /// <summary>The server returned an item I don’t understand.</summary>
    public static string Server_UnknownItem => Get(nameof(Server_UnknownItem));

    /// <summary>(untitled)</summary>
    public static string Server_Untitled => Get(nameof(Server_Untitled));

    /// <summary>Not signed in to the server.</summary>
    public static string Server_NotSignedIn => Get(nameof(Server_NotSignedIn));

    /// <summary>Fallback name for a chapter whose own title is missing.</summary>
    public static string Chapter_Numbered => Get(nameof(Chapter_Numbered));

    /// <summary>Fallback name for a part of an ebook that has no chapter marks.</summary>
    public static string Section_Numbered => Get(nameof(Section_Numbered));
}
