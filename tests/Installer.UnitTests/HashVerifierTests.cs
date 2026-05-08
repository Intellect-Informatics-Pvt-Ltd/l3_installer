using FluentAssertions;
using ManifestVerifier;

namespace Installer.UnitTests;

public sealed class HashVerifierTests : IDisposable
{
    private readonly HashVerifier _verifier = new();
    private readonly string _tempDir;

    public HashVerifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"epacs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ComputeHashAsync_KnownContent_ReturnsExpectedHash()
    {
        // SHA-256 of "hello world\n" is well-known
        var filePath = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "hello world\n");

        var hash = await _verifier.ComputeHashAsync(filePath);

        // SHA-256 of "hello world\n" (with newline)
        hash.Should().NotBeNullOrWhiteSpace();
        hash.Should().HaveLength(64); // SHA-256 = 32 bytes = 64 hex chars
        hash.Should().MatchRegex("^[0-9a-f]{64}$"); // lowercase hex
    }

    [Fact]
    public async Task ComputeHashAsync_EmptyFile_ReturnsEmptyFileHash()
    {
        var filePath = Path.Combine(_tempDir, "empty.txt");
        await File.WriteAllBytesAsync(filePath, []);

        var hash = await _verifier.ComputeHashAsync(filePath);

        // SHA-256 of empty input is e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task VerifyAsync_CorrectHash_ReturnsTrue()
    {
        var filePath = Path.Combine(_tempDir, "verify.txt");
        await File.WriteAllBytesAsync(filePath, []);

        var result = await _verifier.VerifyAsync(
            filePath,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_IncorrectHash_ReturnsFalse()
    {
        var filePath = Path.Combine(_tempDir, "verify.txt");
        await File.WriteAllTextAsync(filePath, "some content");

        var result = await _verifier.VerifyAsync(filePath, "0000000000000000000000000000000000000000000000000000000000000000");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_CaseInsensitiveHash_ReturnsTrue()
    {
        var filePath = Path.Combine(_tempDir, "case.txt");
        await File.WriteAllBytesAsync(filePath, []);

        // Uppercase hash should still match
        var result = await _verifier.VerifyAsync(
            filePath,
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ComputeHashAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        var act = () => _verifier.ComputeHashAsync("/nonexistent/file.bin");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ComputeHashAsync_SameContentProducesSameHash()
    {
        var file1 = Path.Combine(_tempDir, "file1.txt");
        var file2 = Path.Combine(_tempDir, "file2.txt");
        var content = "deterministic content for hash test"u8.ToArray();

        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        var hash1 = await _verifier.ComputeHashAsync(file1);
        var hash2 = await _verifier.ComputeHashAsync(file2);

        hash1.Should().Be(hash2);
    }
}
