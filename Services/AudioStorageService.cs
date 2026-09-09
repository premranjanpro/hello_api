using System.Buffers.Binary;
using System.Text;

namespace PruvaVoice.Api.Services;

public record RecordingMetadata(
    long FileSizeBytes,
    int DurationSeconds,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string AudioFormat,
    DateTime CreatedAt
);

public interface IAudioStorageService
{
    Task<string> SaveRecordingAsync(Guid tenantId, Guid callSessionId, Stream audioStream, string extension = ".wav");
    Task<Stream?> GetRecordingStreamAsync(Guid tenantId, string filename);
    Task<RecordingMetadata?> GetMetadataAsync(Guid tenantId, string filename);
    RecordingMetadata ParseWavHeader(byte[] headerBytes, long totalFileBytes);
    string GetPublicPlaybackUrl(Guid tenantId, string filename);
}

public class LocalStorageAudioService : IAudioStorageService
{
    private readonly string _baseDirectory;

    public LocalStorageAudioService(IConfiguration configuration)
    {
        var configuredPath = configuration["AudioStorage:BasePath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            _baseDirectory = configuredPath;
        }
        else
        {
            _baseDirectory = Path.Combine(Directory.GetCurrentDirectory(), "recordings");
        }

        Directory.CreateDirectory(_baseDirectory);
    }

    private string? LocateRecordingFile(Guid tenantId, string filename)
    {
        var safeFilename = Path.GetFileName(filename);
        var candidates = new[]
        {
            Path.Combine(_baseDirectory, tenantId.ToString(), safeFilename),
            Path.Combine(_baseDirectory, safeFilename),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings", tenantId.ToString(), safeFilename),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recordings", safeFilename),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "ai-agent", "recordings", safeFilename),
            Path.Combine(Directory.GetCurrentDirectory(), "recordings", tenantId.ToString(), safeFilename)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> SaveRecordingAsync(Guid tenantId, Guid callSessionId, Stream audioStream, string extension = ".wav")
    {
        var tenantDir = Path.Combine(_baseDirectory, tenantId.ToString());
        Directory.CreateDirectory(tenantDir);

        var filename = $"{callSessionId:N}{extension}";
        var destinationPath = Path.Combine(tenantDir, filename);

        using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            await audioStream.CopyToAsync(fileStream);
        }

        return filename;
    }

    public Task<Stream?> GetRecordingStreamAsync(Guid tenantId, string filename)
    {
        var filePath = LocateRecordingFile(tenantId, filename);
        if (filePath == null) return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult<Stream?>(stream);
    }

    public async Task<RecordingMetadata?> GetMetadataAsync(Guid tenantId, string filename)
    {
        var filePath = LocateRecordingFile(tenantId, filename);
        if (filePath == null) return null;

        var fileInfo = new FileInfo(filePath);
        var headerBytes = new byte[44];
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = await fs.ReadAsync(headerBytes, 0, 44);
            if (read < 44) return null;
        }

        return ParseWavHeader(headerBytes, fileInfo.Length);
    }

    public RecordingMetadata ParseWavHeader(byte[] headerBytes, long totalFileBytes)
    {
        var riff = Encoding.ASCII.GetString(headerBytes, 0, 4);
        var wave = Encoding.ASCII.GetString(headerBytes, 8, 4);

        if (riff != "RIFF" || wave != "WAVE")
        {
            int fallbackDuration = (int)Math.Max(1, totalFileBytes / (24000 * 2));
            return new RecordingMetadata(
                FileSizeBytes: totalFileBytes,
                DurationSeconds: fallbackDuration,
                SampleRate: 24000,
                Channels: 1,
                BitsPerSample: 16,
                AudioFormat: "RAW_PCM",
                CreatedAt: DateTime.UtcNow
            );
        }

        int audioFormatCode = BinaryPrimitives.ReadInt16LittleEndian(headerBytes.AsSpan(20, 2));
        int channels = BinaryPrimitives.ReadInt16LittleEndian(headerBytes.AsSpan(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(24, 4));
        int byteRate = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(28, 4));
        int bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(headerBytes.AsSpan(34, 2));

        string formatName = audioFormatCode switch
        {
            1 => "PCM_LINEAR",
            6 => "A_LAW",
            7 => "MU_LAW",
            _ => $"FORMAT_{audioFormatCode}"
        };

        int duration = 0;
        if (byteRate > 0)
        {
            long dataSize = Math.Max(0, totalFileBytes - 44);
            duration = (int)(dataSize / byteRate);
        }

        return new RecordingMetadata(
            FileSizeBytes: totalFileBytes,
            DurationSeconds: duration,
            SampleRate: sampleRate,
            Channels: channels,
            BitsPerSample: bitsPerSample,
            AudioFormat: formatName,
            CreatedAt: DateTime.UtcNow
        );
    }

    public string GetPublicPlaybackUrl(Guid tenantId, string filename)
    {
        return $"/api/audio/recordings/{tenantId}/{Uri.EscapeDataString(filename)}";
    }
}
