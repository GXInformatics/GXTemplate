#nullable enable
using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using CleanArchitecture.Blazor.Application.Common.Interfaces.Storage;
using CleanArchitecture.Blazor.Infrastructure.Services.Storage;
using FluentAssertions;
using NUnit.Framework;

namespace CleanArchitecture.Blazor.Application.UnitTests.Storage;

/// <summary>
/// Runs the whole <see cref="FileStorageContractTests"/> suite against a real Azure Blob endpoint,
/// supplied by the Azurite emulator on its default port.
/// </summary>
/// <remarks>
/// The connection string below is Azurite's well-known development account - it is published by
/// Microsoft, it is the same on every machine, and it authenticates against nothing but a local
/// emulator. If no emulator is listening the fixture is IGNORED rather than failed: the provider is
/// then simply not covered on that machine, which is a fact worth reporting rather than a red suite.
/// </remarks>
[TestFixture]
public class AzureBlobFileStorageTests : FileStorageContractTests
{
    private const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    private const int AzuriteBlobPort = 10000;

    private BlobContainerClient _container = null!;
    private AzureBlobFileStorage _storage = null!;

    protected override IFileStorage Storage => _storage;

    [SetUp]
    public async Task SetUp()
    {
        if (!EmulatorIsListening())
        {
            Assert.Ignore($"Azurite is not listening on 127.0.0.1:{AzuriteBlobPort}; the Azure Blob provider is not exercised on this machine.");
        }

        // A container per test, so the contract tests cannot see each other's blobs.
        _container = new BlobContainerClient(AzuriteConnectionString, $"gx-test-{Guid.NewGuid():N}");
        _storage = new AzureBlobFileStorage(_container);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_container is not null) await _container.DeleteIfExistsAsync();
    }

    [Test]
    public async Task TheKeyIsTheBlobName()
    {
        var saved = await _storage.SaveAsync(new Application.Common.Models.FileUploadRequest(
            "report.pdf", Domain.Common.Enums.UploadType.Document, new byte[] { 1, 2, 3 }, overwrite: true));

        saved.Succeeded.Should().BeTrue(saved.ErrorMessage);
        saved.Data!.StorageKey.Should().Be("Documents/report.pdf");

        // The abstraction's key IS the blob name - no translation layer to drift.
        var blob = _container.GetBlobClient("Documents/report.pdf");
        (await blob.ExistsAsync()).Value.Should().BeTrue();

        var properties = await blob.GetPropertiesAsync();
        properties.Value.ContentType.Should().Be("application/pdf");
    }

    // Probed once per run, not once per test: the connect timeout is the whole cost of this fixture
    // on a machine with no emulator, and paying it twelve times is twelve times too many.
    private static readonly Lazy<bool> Available = new(ProbeEmulator, isThreadSafe: true);

    private static bool EmulatorIsListening() => Available.Value;

    private static bool ProbeEmulator()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", AzuriteBlobPort).Wait(TimeSpan.FromSeconds(2))
                   && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
#nullable restore
