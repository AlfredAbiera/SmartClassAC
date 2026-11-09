using System.Security.Cryptography;

namespace SmartClassAC.Services;

internal sealed class EnrollmentLocalProvisioning
{
    private readonly EnrollmentDeviceCredentialStore _credentials;

    public EnrollmentLocalProvisioning(IConfiguration configuration) =>
        _credentials = new EnrollmentDeviceCredentialStore(configuration);

    public async Task<byte[]> GetOrCreateKeyAsync(string deviceId, byte[] masterKey, CancellationToken cancellationToken = default)
    {
        var existing = await _credentials.LoadAsync(deviceId, masterKey, cancellationToken);
        if (existing is not null) return existing;

        var generated = RandomNumberGenerator.GetBytes(EnrollmentMessageAuthentication.KeySize);
        try
        {
            var result = await _credentials.StoreNewAsync(deviceId, generated, masterKey, cancellationToken);
            if (result.Succeeded)
            {
                var stored = generated;
                generated = Array.Empty<byte>();
                return stored;
            }

            var concurrent = await _credentials.LoadAsync(deviceId, masterKey, cancellationToken);
            if (concurrent is not null) return concurrent;
            throw new InvalidOperationException(result.Error ?? "Could not provision the scanner key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generated);
        }
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length != 2 || !EnrollmentMessageAuthentication.IsValidDeviceId(args[1]))
        {
            Console.Error.WriteLine("Specify one numeric scanner ID.");
            return 1;
        }
        if (!Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("This command must be invoked by the local USB provisioning tool.");
            return 1;
        }

        var configured = configuration["EnrollmentSecurity:MasterKey"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            Console.Error.WriteLine("Set EnrollmentSecurity:MasterKey to a persistent, securely stored 32-byte Base64 key.");
            return 1;
        }
        byte[] master;
        try { master = Convert.FromBase64String(configured); }
        catch (FormatException)
        {
            Console.Error.WriteLine("EnrollmentSecurity:MasterKey must be a Base64-encoded 32-byte key.");
            return 1;
        }
        try
        {
            if (master.Length != EnrollmentMessageAuthentication.KeySize)
                throw new InvalidOperationException("EnrollmentSecurity:MasterKey must decode to 32 bytes.");
            var key = await new EnrollmentLocalProvisioning(configuration).GetOrCreateKeyAsync(args[1], master);
            try
            {
                await Console.OpenStandardOutput().WriteAsync(key);
                await Console.OpenStandardOutput().FlushAsync();
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or MySqlConnector.MySqlException)
        {
            Console.Error.WriteLine(DescribeFailure(exception));
            return 1;
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    internal static string DescribeFailure(Exception exception) => exception switch
    {
        MySqlConnector.MySqlException database => $"Enrollment provisioning database failure (error {database.Number}). " +
            (database.Number switch
            {
                1045 => "Database authentication failed. Check the provisioning process's database credentials.",
                1049 => "Configured database does not exist. Use the same database as the running portal.",
                1146 or 1054 => "Required table or column is missing. Apply the portal migrations to the intended database.",
                _ => "Check database availability, permissions and schema. Do not change the master key."
            }),
        CryptographicException => "Enrollment key protection verification failed. The master key may not match this database, " +
            "or a stored scanner key may be damaged. Restore the original master key; do not regenerate it.",
        InvalidOperationException operation when operation.Message is
            "EnrollmentSecurity:MasterKey must decode to 32 bytes." or
            "Set ConnectionStrings:SmartClassMariaDb before provisioning enrollment keys." or
            "The scanner is unknown, inactive, or unpaired." or
            "The scanner has an unresolved fingerprint write." or
            "The scanner already has a key or this key is already in use; replacement is not yet supported." =>
            $"Enrollment key provisioning failed: {operation.Message}",
        _ => "Enrollment key provisioning failed unexpectedly. Check the device, migrations, master key and database."
    };
}
