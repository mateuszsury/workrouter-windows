using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using WorkRouter.Abstractions;
using WorkRouter.Configuration;
using WorkRouter.Infrastructure;
using WorkRouter.Models;

namespace WorkRouter.Sharing;

public sealed class WindowsShareManager : IShareManager
{
    private const string AccountName = "workshare";
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsShareManager(string? programDataRoot = null)
    {
        _stateDirectory = programDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WorkRouter");
        Directory.CreateDirectory(_stateDirectory);
    }

    public async Task<ShareProvisionResult> EnsureAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RouterConfigurationStore.Validate(settings);
            if (!Directory.Exists(settings.SharePath))
            {
                return Failure("share_path_missing", $"Folder {settings.SharePath} nie istnieje.");
            }

            // The share credential is deliberately synchronized with the router
            // passphrase. This keeps a single operator-managed credential and
            // prevents the UI from reporting a password that is not provisioned.
            var synchronizedPassword = settings.Passphrase;
            var provision = await RunProvisionScriptAsync(settings, synchronizedPassword, rotate: false, cancellationToken)
                .ConfigureAwait(false);
            if (!provision.Success)
            {
                return Failure("share_provision_failed", provision.Error ?? "Konfiguracja SMB nie powiodła się.");
            }

            var sid = new SecurityIdentifier(provision.AccountSid!);
            SaveAndApplyDirectoryAcl(settings.SharePath, sid);
            LsaAccountRights.AddDenyInteractiveRights(sid);

            var health = await InspectCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!health.Ready)
            {
                return new ShareProvisionResult(
                    OperationResult.Fail("share_audit_failed", health.Detail),
                    health,
                    null);
            }

            return new ShareProvisionResult(
                OperationResult.Ok("Udział SMB jest gotowy."),
                health,
                provision.AccountCreated ? synchronizedPassword : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure("share_exception", exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareProvisionResult> RotatePasswordAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RouterConfigurationStore.Validate(settings);
            var password = settings.Passphrase;
            var provision = await RunProvisionScriptAsync(settings, password, rotate: true, cancellationToken)
                .ConfigureAwait(false);
            var health = await InspectCoreAsync(settings, cancellationToken).ConfigureAwait(false);
            if (!provision.Success || !health.Ready)
            {
                return new ShareProvisionResult(
                    OperationResult.Fail("password_rotation_failed", provision.Error ?? health.Detail),
                    health,
                    null);
            }

            return new ShareProvisionResult(
                OperationResult.Ok("Hasło konta workshare zostało zmienione."),
                health,
                password);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShareHealth> InspectAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InspectCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ShareHealth> InspectCoreAsync(RouterSettings settings, CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $account = "$env:COMPUTERNAME\workshare"
            $administrators = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value
            $user = Get-LocalUser -Name 'workshare' -ErrorAction SilentlyContinue
            $share = Get-SmbShare -Name 'Firmowe' -ErrorAction SilentlyContinue
            $targetAccess = @()
            if ($share) { $targetAccess = @(Get-SmbShareAccess -Name 'Firmowe') }
            $otherFailures = @()
            foreach ($other in @(Get-SmbShare | Where-Object {
                $_.Name -notin @('Firmowe','IPC$') -and ((-not $_.Special) -or $_.Name -eq 'print$')
            })) {
                $deny = @(Get-SmbShareAccess -Name $other.Name -ErrorAction SilentlyContinue |
                    Where-Object { $_.AccountName -eq $account -and $_.AccessControlType -eq 'Deny' })
                if ($deny.Count -eq 0) { $otherFailures += $other.Name }
            }
            $unexpectedTargetAllows = @($targetAccess | Where-Object {
                $_.AccessControlType -eq 'Allow' -and
                $_.AccountName -notin @($account, $administrators)
            })
            [pscustomobject]@{
                AccountReady = [bool]($user -and $user.Enabled -and $user.PasswordRequired)
                ShareExists = [bool]$share
                PathMatches = [bool]($share -and $share.Path -eq 'E:\Firmowe')
                EncryptData = [bool]($share -and $share.EncryptData)
                AccountAllowed = [bool](@($targetAccess | Where-Object {
                    $_.AccountName -eq $account -and $_.AccessControlType -eq 'Allow' -and $_.AccessRight -in @('Change','Full')
                }).Count -gt 0)
                ExclusiveShareAccess = [bool]($unexpectedTargetAllows.Count -eq 0)
                OtherFailures = @($otherFailures)
            } | ConvertTo-Json -Compress
            """;

        var result = await PowerShellRunner.RunAsync(script, null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return NotReady("Audyt SMB nie powiódł się: " + CleanError(result.StandardError));
        }

        var audit = DeserializeLastJson<ShareAudit>(result.StandardOutput);
        var aclReady = IsAclReady(settings.SharePath);
        var otherDenied = audit.OtherFailures is { Length: 0 };
        var ready = audit.AccountReady && audit.ShareExists && audit.PathMatches && audit.EncryptData &&
                    audit.AccountAllowed && audit.ExclusiveShareAccess && aclReady && otherDenied;
        var detail = ready
            ? "Konto, ACL, szyfrowanie i blokady pozostałych udziałów są aktywne."
            : $"Braki: account={audit.AccountReady}, share={audit.ShareExists && audit.PathMatches}, shareAccess={audit.AccountAllowed && audit.ExclusiveShareAccess}, encryption={audit.EncryptData}, acl={aclReady}, otherShares={otherDenied}.";
        return new ShareHealth(
            ready,
            audit.AccountReady,
            aclReady,
            audit.EncryptData,
            otherDenied,
            @"\\<brama-WORK>\Firmowe",
            detail);
    }

    private async Task<ProvisionOutput> RunProvisionScriptAsync(
        RouterSettings settings,
        string password,
        bool rotate,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $name = 'workshare'
            $account = "$env:COMPUTERNAME\$name"
            $administrators = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value
            $securePassword = ConvertTo-SecureString $env:WORKROUTER_SHARE_PASSWORD -AsPlainText -Force
            $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
            $created = $false
            if (-not $user) {
                $user = New-LocalUser -Name $name -Password $securePassword -PasswordNeverExpires -UserMayNotChangePassword -Description 'WorkRouter: dostęp wyłącznie do udziału Firmowe'
                $created = $true
            } elseif ($env:WORKROUTER_ROTATE -eq '1' -or $env:WORKROUTER_SYNC -eq '1') {
                Set-LocalUser -Name $name -Password $securePassword -PasswordNeverExpires $true -UserMayChangePassword $false
            }
            if (-not $user.Enabled) { Enable-LocalUser -Name $name }

            # New-LocalUser can leave UF_PASSWD_NOTREQD set even when a password
            # was supplied. Clear that flag explicitly so the account cannot be
            # treated as password-optional and so the post-provision audit is
            # based on the actual SAM policy rather than only on our input.
            $userEntry = [ADSI]"WinNT://$env:COMPUTERNAME/$name,user"
            $userFlags = [int]$userEntry.UserFlags.Value
            $userEntry.Put('UserFlags', ($userFlags -band (-bnot 0x20)))
            $userEntry.SetInfo()
            $user = Get-LocalUser -Name $name

            $share = Get-SmbShare -Name 'Firmowe' -ErrorAction SilentlyContinue
            if ($share -and $share.Path -ne 'E:\Firmowe') { throw 'Udział Firmowe wskazuje inny katalog.' }
            if (-not $share) {
                New-SmbShare -Name 'Firmowe' -Path 'E:\Firmowe' -ChangeAccess $account -FullAccess $administrators -EncryptData $true -CachingMode None -FolderEnumerationMode AccessBased | Out-Null
            } else {
                Set-SmbShare -Name 'Firmowe' -EncryptData $true -CachingMode None -FolderEnumerationMode AccessBased -Force | Out-Null
                Grant-SmbShareAccess -Name 'Firmowe' -AccountName $account -AccessRight Change -Force | Out-Null
                Grant-SmbShareAccess -Name 'Firmowe' -AccountName $administrators -AccessRight Full -Force | Out-Null
            }
            foreach ($entry in @(Get-SmbShareAccess -Name 'Firmowe' | Where-Object {
                $_.AccessControlType -eq 'Allow' -and $_.AccountName -notin @($account, $administrators)
            })) {
                Revoke-SmbShareAccess -Name 'Firmowe' -AccountName $entry.AccountName -Force -ErrorAction Stop | Out-Null
            }

            $denyFailures = @()
            foreach ($other in @(Get-SmbShare | Where-Object {
                $_.Name -notin @('Firmowe','IPC$') -and ((-not $_.Special) -or $_.Name -eq 'print$')
            })) {
                try { Block-SmbShareAccess -Name $other.Name -AccountName $account -Force -ErrorAction Stop | Out-Null }
                catch { $denyFailures += "$($other.Name): $($_.Exception.Message)" }
            }
            if ($denyFailures.Count -gt 0) { throw ('Nie udało się zablokować innych udziałów: ' + ($denyFailures -join '; ')) }

            $user = Get-LocalUser -Name $name
            [pscustomobject]@{
                Success = $true
                AccountCreated = $created
                AccountSid = $user.SID.Value
                Error = $null
            } | ConvertTo-Json -Compress
            """;

        var environment = new Dictionary<string, string?>
        {
            ["WORKROUTER_SHARE_PASSWORD"] = password,
            ["WORKROUTER_ROTATE"] = rotate ? "1" : "0",
            ["WORKROUTER_SYNC"] = "1"
        };
        var result = await PowerShellRunner.RunAsync(script, environment, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new ProvisionOutput(false, false, null, CleanError(result.StandardError));
        }

        return DeserializeLastJson<ProvisionOutput>(result.StandardOutput);
    }

    private void SaveAndApplyDirectoryAcl(string path, SecurityIdentifier accountSid)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(AccessControlSections.All);
        var baselinePath = Path.Combine(_stateDirectory, "firmowe-acl-baseline.txt");
        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath, security.GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidOperationException("Nie można ustalić właściciela katalogu Firmowe.");
        security.SetAccessRuleProtection(true, false);
        foreach (AuthorizationRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific((FileSystemAccessRule)rule);
        }

        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        AddDirectoryRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        AddDirectoryRule(security, owner, FileSystemRights.FullControl);
        AddDirectoryRule(security, accountSid, FileSystemRights.Modify | FileSystemRights.Synchronize);
        directory.SetAccessControl(security);
    }

    private static void AddDirectoryRule(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static bool IsAclReady(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            var rules = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .ToArray();
            var broadSids = new[]
            {
                new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value
            };
            return rules.All(rule => rule.AccessControlType != AccessControlType.Allow ||
                                     rule.IdentityReference is not SecurityIdentifier sid ||
                                     !broadSids.Contains(sid.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static T DeserializeLastJson<T>(string output)
    {
        var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(candidate => candidate.TrimStart().StartsWith('{'));
        return line is null
            ? throw new JsonException("PowerShell nie zwrócił danych JSON.")
            : JsonSerializer.Deserialize<T>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? throw new JsonException("Nie można odczytać danych JSON PowerShell.");
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#%+-_";
        return string.Create(28, alphabet, static (span, chars) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var index = 0; index < span.Length; index++)
            {
                span[index] = chars[bytes[index] % chars.Length];
            }
        });
    }

    private static string CleanError(string error) =>
        string.Join(' ', error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static ShareProvisionResult Failure(string code, string message) =>
        new(OperationResult.Fail(code, message), NotReady(message), null);

    private static ShareHealth NotReady(string detail) =>
        new(false, false, false, false, false, @"\\<brama-WORK>\Firmowe", detail);

    private sealed record ProvisionOutput(bool Success, bool AccountCreated, string? AccountSid, string? Error);
    private sealed record ShareAudit(
        bool AccountReady,
        bool ShareExists,
        bool PathMatches,
        bool EncryptData,
        bool AccountAllowed,
        bool ExclusiveShareAccess,
        string[]? OtherFailures);
}
