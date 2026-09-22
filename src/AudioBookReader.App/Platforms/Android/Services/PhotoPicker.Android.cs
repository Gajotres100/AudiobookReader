namespace AudioBookReader.App.Services;

public partial class PhotoPicker
{
    public partial async Task<byte[]?> PickFromGalleryAsync()
    {
        var uri = await MainActivity.PickImageAsync();
        if (uri is null) return null;

        try
        {
            var resolver = global::Android.App.Application.Context.ContentResolver;

            // Read here and now, into memory. The grant the photo picker gives covers this process
            // and this picture only, so there is no later moment to read it in — and a page
            // photograph is a few megabytes, which is nothing next to the book already open.
            await using var stream = resolver?.OpenInputStream(uri);
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Error($"reading the chosen picture {uri}", ex);
            return null;
        }
    }
}
