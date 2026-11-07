using System.Buffers.Binary;
using System.IO.Compression;

namespace VectorSetsDemo;

/// <summary>
/// The MNIST test set (10,000 handwritten digits, 28×28 grayscale), fetched once into
/// <c>data/</c> and parsed from the original IDX format. Public-domain-style research data,
/// served from the CVDF mirror.
/// </summary>
static class Mnist
{
    private const string BaseUrl = "https://storage.googleapis.com/cvdf-datasets/mnist/";
    private const string ImagesFile = "t10k-images-idx3-ubyte.gz";
    private const string LabelsFile = "t10k-labels-idx1-ubyte.gz";
    public const int Width = 28, Height = 28, Pixels = Width * Height;

    public sealed record Dataset(byte[][] Images, byte[] Labels)
    {
        public int Count => Labels.Length;

        /// <summary>Pixels scaled to [0,1] floats: the "embedding" is the image itself.</summary>
        public float[] Vector(int index)
        {
            var img = Images[index];
            var v = new float[Pixels];
            for (int i = 0; i < Pixels; i++) { v[i] = img[i] / 255f; }
            return v;
        }
    }

    public static async Task<Dataset> LoadAsync(string dataDir, Action<string> progress)
    {
        Directory.CreateDirectory(dataDir);
        string imagesPath = Path.Combine(dataDir, ImagesFile);
        string labelsPath = Path.Combine(dataDir, LabelsFile);
        using (var http = new HttpClient())
        {
            await DownloadAsync(http, imagesPath, progress);
            await DownloadAsync(http, labelsPath, progress);
        }

        progress("parsing IDX files...");
        byte[] images = Decompress(imagesPath), labels = Decompress(labelsPath);

        if (BinaryPrimitives.ReadInt32BigEndian(images) != 2051 ||
            BinaryPrimitives.ReadInt32BigEndian(labels) != 2049)
        {
            throw new InvalidDataException("not MNIST IDX data (bad magic); delete data/ and retry");
        }
        int count = BinaryPrimitives.ReadInt32BigEndian(images.AsSpan(4));
        int rows = BinaryPrimitives.ReadInt32BigEndian(images.AsSpan(8));
        int cols = BinaryPrimitives.ReadInt32BigEndian(images.AsSpan(12));
        if (rows != Height || cols != Width || count != BinaryPrimitives.ReadInt32BigEndian(labels.AsSpan(4)))
        {
            throw new InvalidDataException("unexpected MNIST shape; delete data/ and retry");
        }

        var imageArray = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            imageArray[i] = images.AsSpan(16 + i * Pixels, Pixels).ToArray();
        }
        return new Dataset(imageArray, labels.AsSpan(8, count).ToArray());
    }

    private static async Task DownloadAsync(HttpClient http, string path, Action<string> progress)
    {
        if (File.Exists(path)) return;
        string name = Path.GetFileName(path);
        progress($"downloading {name}...");
        byte[] data = await http.GetByteArrayAsync(BaseUrl + name);
        await File.WriteAllBytesAsync(path + ".tmp", data);
        File.Move(path + ".tmp", path, overwrite: true);
        progress($"downloaded {name} ({data.Length / 1024}KB)");
    }

    private static byte[] Decompress(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}
