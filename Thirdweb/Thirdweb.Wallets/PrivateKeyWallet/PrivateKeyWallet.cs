using System.Text;
using Nethereum.ABI.EIP712;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Hex.HexTypes;
using Nethereum.Model;
using Nethereum.Signer;
using Nethereum.Signer.EIP712;

namespace Thirdweb;

/// <summary>
/// Represents a wallet that uses a private key for signing transactions and messages.
/// </summary>
public class PrivateKeyWallet : IThirdwebWallet
{
    /// <summary>
    /// Gets the Thirdweb client associated with the wallet.
    /// </summary>
    public ThirdwebClient Client { get; }

    /// <summary>
    /// Gets the account type of the wallet.
    /// </summary>
    public ThirdwebAccountType AccountType => ThirdwebAccountType.PrivateKeyAccount;

    /// <summary>
    /// The Ethereum EC key used by the wallet.
    /// </summary>
    protected EthECKey EcKey { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PrivateKeyWallet"/> class.
    /// </summary>
    /// <param name="client">The Thirdweb client.</param>
    /// <param name="key">The Ethereum EC key.</param>
    protected PrivateKeyWallet(ThirdwebClient client, EthECKey key)
    {
        this.Client = client;
        this.EcKey = key;
    }

    /// <summary>
    /// Creates a new instance of <see cref="PrivateKeyWallet"/> using the specified private key.
    /// </summary>
    /// <param name="client">The Thirdweb client.</param>
    /// <param name="privateKeyHex">The private key in hexadecimal format.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created <see cref="PrivateKeyWallet"/>.</returns>
    public static Task<PrivateKeyWallet> Create(ThirdwebClient client, string privateKeyHex)
    {
        return string.IsNullOrEmpty(privateKeyHex)
            ? throw new ArgumentNullException(nameof(privateKeyHex), "Private key cannot be null or empty.")
            : Task.FromResult(new PrivateKeyWallet(client, new EthECKey(privateKeyHex)));
    }

    /// <summary>
    /// Generates a new instance of <see cref="PrivateKeyWallet"/> with a random private key.
    /// </summary>
    /// <param name="client">The Thirdweb client.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the created <see cref="PrivateKeyWallet"/>.</returns>
    public static Task<PrivateKeyWallet> Generate(ThirdwebClient client)
    {
        return Task.FromResult(new PrivateKeyWallet(client, EthECKey.GenerateKey()));
    }

    /// <summary>
    /// Gets the address of the wallet.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the address of the wallet.</returns>
    public virtual Task<string> GetAddress()
    {
        return Task.FromResult(this.EcKey.GetPublicAddress().ToChecksumAddress());
    }

    /// <summary>
    /// Signs a message using the wallet's private key.
    /// </summary>
    /// <param name="rawMessage">The message to sign.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed message.</returns>
    public virtual Task<string> EthSign(byte[] rawMessage)
    {
        if (rawMessage == null)
        {
            throw new ArgumentNullException(nameof(rawMessage), "Message to sign cannot be null.");
        }

        var signer = new MessageSigner();
        var signature = signer.Sign(rawMessage, this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Signs a message using the wallet's private key.
    /// </summary>
    /// <param name="message">The message to sign.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed message.</returns>
    public virtual Task<string> EthSign(string message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message), "Message to sign cannot be null.");
        }

        var signer = new MessageSigner();
        var signature = signer.Sign(Encoding.UTF8.GetBytes(message), this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Recovers the address from a signed message using Ethereum's signing method.
    /// </summary>
    /// <param name="message">The UTF-8 encoded message.</param>
    /// <param name="signature">The signature.</param>
    /// <returns>The recovered address.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual Task<string> RecoverAddressFromEthSign(string message, string signature)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message), "Message to sign cannot be null.");
        }

        if (signature == null)
        {
            throw new ArgumentNullException(nameof(signature), "Signature cannot be null.");
        }

        var signer = new MessageSigner();
        var address = signer.EcRecover(Encoding.UTF8.GetBytes(message), signature);
        return Task.FromResult(address);
    }

    /// <summary>
    /// Signs a message using the wallet's private key with personal sign.
    /// </summary>
    /// <param name="rawMessage">The message to sign.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed message.</returns>
    public virtual Task<string> PersonalSign(byte[] rawMessage)
    {
        if (rawMessage == null)
        {
            throw new ArgumentNullException(nameof(rawMessage), "Message to sign cannot be null.");
        }

        var signer = new EthereumMessageSigner();
        var signature = signer.Sign(rawMessage, this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Signs a message using the wallet's private key with personal sign.
    /// </summary>
    /// <param name="message">The message to sign.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed message.</returns>
    public virtual Task<string> PersonalSign(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentNullException(nameof(message), "Message to sign cannot be null.");
        }

        var signer = new EthereumMessageSigner();
        var signature = signer.EncodeUTF8AndSign(message, this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Recovers the address from a signed message using personal signing.
    /// </summary>
    /// <param name="message">The UTF-8 encoded and prefixed message.</param>
    /// <param name="signature">The signature.</param>
    /// <returns>The recovered address.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual Task<string> RecoverAddressFromPersonalSign(string message, string signature)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentNullException(nameof(message), "Message to sign cannot be null.");
        }

        if (string.IsNullOrEmpty(signature))
        {
            throw new ArgumentNullException(nameof(signature), "Signature cannot be null.");
        }

        var signer = new EthereumMessageSigner();
        var address = signer.EncodeUTF8AndEcRecover(message, signature);
        return Task.FromResult(address);
    }

    /// <summary>
    /// Signs typed data (EIP-712) using the wallet's private key.
    /// </summary>
    /// <param name="json">The JSON string representing the typed data.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed data.</returns>
    public virtual Task<string> SignTypedDataV4(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            throw new ArgumentNullException(nameof(json), "Json to sign cannot be null.");
        }

        var signer = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(json, this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Signs typed data (EIP-712) using the wallet's private key.
    /// </summary>
    /// <typeparam name="T">The type of the data to sign.</typeparam>
    /// <typeparam name="TDomain">The type of the domain.</typeparam>
    /// <param name="data">The data to sign.</param>
    /// <param name="typedData">The typed data.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed data.</returns>
    public virtual Task<string> SignTypedDataV4<T, TDomain>(T data, TypedData<TDomain> typedData)
        where TDomain : IDomain
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data), "Data to sign cannot be null.");
        }

        var signer = new Eip712TypedDataSigner();
        var signature = signer.SignTypedDataV4(data, typedData, this.EcKey);
        return Task.FromResult(signature);
    }

    /// <summary>
    /// Recovers the address from a signed message using typed data (version 4).
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TDomain"></typeparam>
    /// <param name="data">The data to sign.</param>
    /// <param name="typedData">The typed data.</param>
    /// <param name="signature">The signature.</param>
    /// <returns>The recovered address.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual Task<string> RecoverAddressFromTypedDataV4<T, TDomain>(T data, TypedData<TDomain> typedData, string signature)
        where TDomain : IDomain
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data), "Data to sign cannot be null.");
        }

        if (typedData == null)
        {
            throw new ArgumentNullException(nameof(typedData), "Typed data cannot be null.");
        }

        if (signature == null)
        {
            throw new ArgumentNullException(nameof(signature), "Signature cannot be null.");
        }

        var signer = new Eip712TypedDataSigner();
        var address = signer.RecoverFromSignatureV4(data, typedData, signature);
        return Task.FromResult(address);
    }

    /// <summary>
    /// Signs a transaction using the wallet's private key.
    /// </summary>
    /// <param name="transaction">The transaction to sign.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the signed transaction.</returns>
    public virtual Task<string> SignTransaction(ThirdwebTransactionInput transaction)
    {
        if (transaction == null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        var nonce = transaction.Nonce ?? throw new ArgumentNullException(nameof(transaction), "Transaction nonce has not been set");

        var gasLimit = transaction.Gas;
        var value = transaction.Value ?? new HexBigInteger(0);

        string signedTransaction;

        if (transaction.GasPrice != null)
        {
            var gasPrice = transaction.GasPrice;
            var legacySigner = new LegacyTransactionSigner();
            signedTransaction = legacySigner.SignTransaction(
                this.EcKey.GetPrivateKey(),
                transaction.ChainId.Value,
                transaction.To,
                value.Value,
                nonce,
                gasPrice.Value,
                gasLimit.Value,
                transaction.Data
            );
        }
        else
        {
            if (transaction.MaxPriorityFeePerGas == null || transaction.MaxFeePerGas == null)
            {
                throw new InvalidOperationException("Transaction MaxPriorityFeePerGas and MaxFeePerGas must be set for EIP-1559 transactions");
            }
            var maxPriorityFeePerGas = transaction.MaxPriorityFeePerGas.Value;
            var maxFeePerGas = transaction.MaxFeePerGas.Value;
            var transaction1559 = new Transaction1559(transaction.ChainId.Value, nonce, maxPriorityFeePerGas, maxFeePerGas, gasLimit, transaction.To, value, transaction.Data, null);

            var signer = new Transaction1559Signer();
            _ = signer.SignTransaction(this.EcKey, transaction1559);
            signedTransaction = transaction1559.GetRLPEncoded().ToHex();
        }

        return Task.FromResult("0x" + signedTransaction);
    }

    /// <summary>
    /// Checks if the wallet is connected.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result indicates whether the wallet is connected.</returns>
    public virtual Task<bool> IsConnected()
    {
        return Task.FromResult(this.EcKey != null);
    }

    /// <summary>
    /// Disconnects the wallet.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public virtual Task Disconnect()
    {
        this.EcKey = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Throws an exception because sending transactions is not supported for private key wallets.
    /// </summary>
    /// <param name="transaction">The transaction to send.</param>
    /// <returns>Throws an InvalidOperationException.</returns>
    public Task<string> SendTransaction(ThirdwebTransactionInput transaction)
    {
        throw new InvalidOperationException("SendTransaction is not supported for private key wallets, please use the unified Contract or ThirdwebTransaction APIs.");
    }

    public Task<ThirdwebTransactionReceipt> ExecuteTransaction(ThirdwebTransactionInput transactionInput)
    {
        throw new InvalidOperationException("ExecuteTransaction is not supported for private key wallets, please use the unified Contract or ThirdwebTransaction APIs.");
    }
}
