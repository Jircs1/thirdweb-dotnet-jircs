using Nethereum.Web3.Accounts;

namespace Thirdweb.EWS;

internal partial class EmbeddedWallet
{
    internal string GetCurrentAuthToken()
    {
        return this._localStorage.Data?.AuthToken;
    }

    internal async Task<VerifyResult> PostAuthSetup(Server.VerifyResult result, string twManagedRecoveryCodeOverride, string authProvider)
    {
        var walletUserId = result.WalletUserId;
        var authToken = result.AuthToken;
        var emailAddress = result.Email;
        var phoneNumber = result.PhoneNumber;

        var mainRecoveryCode = (twManagedRecoveryCodeOverride ?? result.RecoveryCode) ?? throw new InvalidOperationException("Server failed to return recovery code.");

        (var account, var deviceShare) = result.IsNewUser
            ? await this.CreateAccountAsync(result.AuthToken, mainRecoveryCode).ConfigureAwait(false)
            : await this.RecoverAccountAsync(result.AuthToken, mainRecoveryCode).ConfigureAwait(false);
        var user = await this.MakeUserAsync(emailAddress, phoneNumber, account, authToken, walletUserId, deviceShare, authProvider).ConfigureAwait(false);
        return new VerifyResult(user, mainRecoveryCode);
    }

    public async Task SignOutAsync()
    {
        this._user = null;
        await this._localStorage.RemoveAuthTokenAsync().ConfigureAwait(false);
    }

    public async Task<User> GetUserAsync(string email, string phone, string authProvider)
    {
        email = email?.ToLower();

        if (this._user != null)
        {
            return this._user;
        }
        else if (this._localStorage.Data?.AuthToken == null)
        {
            throw new InvalidOperationException("User is not signed in");
        }

        var userWallet = await this._server.FetchUserDetailsAsync(null, this._localStorage.Data.AuthToken).ConfigureAwait(false);
        switch (userWallet.Status)
        {
            case "Logged Out":
                throw new InvalidOperationException("User is logged out");
            case "Logged In, Wallet Uninitialized":
                throw new InvalidOperationException("User is logged in but wallet is uninitialized");
            case "Logged In, Wallet Initialized":
                if (string.IsNullOrEmpty(this._localStorage.Data?.DeviceShare))
                {
                    throw new InvalidOperationException("User is logged in but wallet is uninitialized");
                }

                var authShare = await this._server.FetchAuthShareAsync(this._localStorage.Data.AuthToken).ConfigureAwait(false);
                var emailAddress = userWallet.StoredToken?.AuthDetails.Email;
                var phoneNumber = userWallet.StoredToken?.AuthDetails.PhoneNumber;

                if ((email != null && email != emailAddress) || (phone != null && phone != phoneNumber))
                {
                    throw new InvalidOperationException("User email or phone number do not match");
                }
                else if (email == null && this._localStorage.Data.AuthProvider != authProvider)
                {
                    throw new InvalidOperationException($"User auth provider does not match. Expected {this._localStorage.Data.AuthProvider}, got {authProvider}");
                }
                else if (authShare == null)
                {
                    throw new InvalidOperationException("Server failed to return auth share");
                }

                this._user = new User(MakeAccountFromShares(new[] { authShare, this._localStorage.Data.DeviceShare }), emailAddress, phoneNumber);
                return this._user;
            default:
                break;
        }
        throw new InvalidOperationException($"Unexpected user status '{userWallet.Status}'");
    }

    private async Task<User> MakeUserAsync(string emailAddress, string phoneNumber, Account account, string authToken, string walletUserId, string deviceShare, string authProvider)
    {
        var data = new LocalStorage.DataStorage(authToken, deviceShare, emailAddress, phoneNumber, walletUserId, authProvider);
        await this._localStorage.SaveDataAsync(data).ConfigureAwait(false);
        this._user = new User(account, emailAddress, phoneNumber);
        return this._user;
    }

    private async Task<(Account account, string deviceShare)> CreateAccountAsync(string authToken, string recoveryCode)
    {
        var secret = Secrets.Random(KEY_SIZE);

        (var deviceShare, var recoveryShare, var authShare) = CreateShares(secret);
        var encryptedRecoveryShare = await this.EncryptShareAsync(recoveryShare, recoveryCode).ConfigureAwait(false);
        Account account = new(secret);
        await this._server.StoreAddressAndSharesAsync(account.Address, authShare, encryptedRecoveryShare, authToken).ConfigureAwait(false);
        return (account, deviceShare);
    }

    private async Task<(Account account, string deviceShare)> RecoverAccountAsync(string authToken, string recoveryCode)
    {
        (var authShare, var encryptedRecoveryShare) = await this._server.FetchAuthAndRecoverySharesAsync(authToken).ConfigureAwait(false);

        var recoveryShare = await DecryptShareAsync(encryptedRecoveryShare, recoveryCode).ConfigureAwait(false);

        var account = MakeAccountFromShares(authShare, recoveryShare);
        Secrets secrets = new();
        var deviceShare = secrets.NewShare(DEVICE_SHARE_ID, new[] { authShare, recoveryShare });
        return (account, deviceShare);
    }

    public class VerifyResult
    {
        public User User { get; }
        public bool CanRetry { get; }
        public string MainRecoveryCode { get; }
        public bool? WasEmailed { get; }

        public VerifyResult(User user, string mainRecoveryCode)
        {
            this.User = user;
            this.MainRecoveryCode = mainRecoveryCode;
        }

        public VerifyResult(bool canRetry)
        {
            this.CanRetry = canRetry;
        }
    }
}
