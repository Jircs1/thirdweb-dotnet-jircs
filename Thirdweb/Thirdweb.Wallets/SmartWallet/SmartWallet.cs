﻿using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethereum.ABI;
using Nethereum.ABI.EIP712;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Util;
using Newtonsoft.Json;
using Thirdweb.AccountAbstraction;

namespace Thirdweb;

public enum TokenPaymaster
{
    NONE,
    BASE_USDC,
}

public class SmartWallet : IThirdwebWallet
{
    public ThirdwebClient Client { get; }

    public ThirdwebAccountType AccountType => ThirdwebAccountType.SmartAccount;

    public bool IsDeploying { get; private set; }

    private readonly IThirdwebWallet _personalAccount;
    private readonly bool _gasless;
    private readonly ThirdwebContract _factoryContract;
    private ThirdwebContract _accountContract;
    private readonly ThirdwebContract _entryPointContract;
    private readonly BigInteger _chainId;
    private readonly string _bundlerUrl;
    private readonly string _paymasterUrl;
    private readonly string _erc20PaymasterAddress;
    private readonly string _erc20PaymasterToken;
    private readonly BigInteger _erc20PaymasterStorageSlot;
    private bool _isApproving;
    private bool _isApproved;

    private struct TokenPaymasterConfig
    {
        public BigInteger ChainId;
        public string PaymasterAddress;
        public string TokenAddress;
        public BigInteger BalanceStorageSlot;
    }

    private static readonly Dictionary<TokenPaymaster, TokenPaymasterConfig> _tokenPaymasterConfig =
        new()
        {
            {
                TokenPaymaster.NONE,
                new TokenPaymasterConfig()
                {
                    ChainId = 0,
                    PaymasterAddress = null,
                    TokenAddress = null,
                    BalanceStorageSlot = 0
                }
            },
            {
                TokenPaymaster.BASE_USDC,
                new TokenPaymasterConfig()
                {
                    ChainId = 8453,
                    PaymasterAddress = "0x0c6199eE133EB4ff8a6bbD03370336C5A5d9D536",
                    TokenAddress = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
                    BalanceStorageSlot = 9
                }
            }
        };

    private bool UseERC20Paymaster => !string.IsNullOrEmpty(this._erc20PaymasterAddress) && !string.IsNullOrEmpty(this._erc20PaymasterToken);

    protected SmartWallet(
        IThirdwebWallet personalAccount,
        bool gasless,
        BigInteger chainId,
        string bundlerUrl,
        string paymasterUrl,
        ThirdwebContract entryPointContract,
        ThirdwebContract factoryContract,
        ThirdwebContract accountContract,
        string erc20PaymasterAddress,
        string erc20PaymasterToken,
        BigInteger erc20PaymasterStorageSlot
    )
    {
        this.Client = personalAccount.Client;

        this._personalAccount = personalAccount;
        this._gasless = gasless;
        this._chainId = chainId;
        this._bundlerUrl = bundlerUrl;
        this._paymasterUrl = paymasterUrl;
        this._entryPointContract = entryPointContract;
        this._factoryContract = factoryContract;
        this._accountContract = accountContract;
        this._erc20PaymasterAddress = erc20PaymasterAddress;
        this._erc20PaymasterToken = erc20PaymasterToken;
        this._erc20PaymasterStorageSlot = erc20PaymasterStorageSlot;
    }

    public static async Task<SmartWallet> Create(
        IThirdwebWallet personalWallet,
        BigInteger chainId,
        bool gasless = true,
        string factoryAddress = null,
        string accountAddressOverride = null,
        string entryPoint = null,
        string bundlerUrl = null,
        string paymasterUrl = null,
        TokenPaymaster tokenPaymaster = TokenPaymaster.NONE
    )
    {
        if (!await personalWallet.IsConnected())
        {
            throw new InvalidOperationException("SmartAccount.Connect: Personal account must be connected.");
        }

        entryPoint ??= Constants.ENTRYPOINT_ADDRESS_V06;

        var entryPointVersion = Utils.GetEntryPointVersion(entryPoint);

        bundlerUrl ??= entryPointVersion == 6 ? $"https://{chainId}.bundler.thirdweb.com" : $"https://{chainId}.bundler.thirdweb.com/v2";
        paymasterUrl ??= entryPointVersion == 6 ? $"https://{chainId}.bundler.thirdweb.com" : $"https://{chainId}.bundler.thirdweb.com/v2";
        factoryAddress ??= entryPointVersion == 6 ? Constants.DEFAULT_FACTORY_ADDRESS_V06 : Constants.DEFAULT_FACTORY_ADDRESS_V07;

        ThirdwebContract entryPointContract = null;
        ThirdwebContract factoryContract = null;
        ThirdwebContract accountContract = null;

        if (!Utils.IsZkSync(chainId))
        {
            var entryPointAbi = entryPointVersion == 6 ? Constants.ENTRYPOINT_V06_ABI : Constants.ENTRYPOINT_V07_ABI;
            var factoryAbi = entryPointVersion == 6 ? Constants.FACTORY_V06_ABI : Constants.FACTORY_V07_ABI;
            var accountAbi = entryPointVersion == 6 ? Constants.ACCOUNT_V06_ABI : Constants.ACCOUNT_V07_ABI;

            entryPointContract = await ThirdwebContract.Create(personalWallet.Client, entryPoint, chainId, entryPointAbi);
            factoryContract = await ThirdwebContract.Create(personalWallet.Client, factoryAddress, chainId, factoryAbi);

            var personalAddress = await personalWallet.GetAddress();
            var accountAddress = accountAddressOverride ?? await ThirdwebContract.Read<string>(factoryContract, "getAddress", personalAddress, Array.Empty<byte>());

            accountContract = await ThirdwebContract.Create(personalWallet.Client, accountAddress, chainId, accountAbi);
        }

        var erc20PmInfo = _tokenPaymasterConfig[tokenPaymaster];

        if (tokenPaymaster != TokenPaymaster.NONE)
        {
            if (entryPointVersion != 7)
            {
                throw new InvalidOperationException("Token paymasters are only supported in entry point version 7.");
            }
            if (erc20PmInfo.ChainId != chainId)
            {
                throw new InvalidOperationException("Token paymaster chain ID does not match the smart account chain ID.");
            }

            // TODO: Re-enable token paymasters
            throw new InvalidOperationException("Token paymasters are currently disabled.");
        }

        return new SmartWallet(
            personalWallet,
            gasless,
            chainId,
            bundlerUrl,
            paymasterUrl,
            entryPointContract,
            factoryContract,
            accountContract,
            erc20PmInfo.PaymasterAddress,
            erc20PmInfo.TokenAddress,
            erc20PmInfo.BalanceStorageSlot
        );
    }

    public async Task<bool> IsDeployed()
    {
        if (Utils.IsZkSync(this._chainId))
        {
            return true;
        }

        var code = await ThirdwebRPC.GetRpcInstance(this.Client, this._chainId).SendRequestAsync<string>("eth_getCode", this._accountContract.Address, "latest");
        return code != "0x";
    }

    public async Task<string> SendTransaction(ThirdwebTransactionInput transactionInput)
    {
        if (transactionInput == null)
        {
            throw new InvalidOperationException("SmartAccount.SendTransaction: Transaction input is required.");
        }

        var transaction = await ThirdwebTransaction.Create(Utils.IsZkSync(this._chainId) ? this._personalAccount : this, transactionInput, this._chainId);
        transaction = await ThirdwebTransaction.Prepare(transaction);
        transactionInput = transaction.Input;

        if (Utils.IsZkSync(this._chainId))
        {
            if (this._gasless)
            {
                (var paymaster, var paymasterInput) = await this.ZkPaymasterData(transactionInput);
                transaction = transaction.SetZkSyncOptions(new ZkSyncOptions(paymaster: paymaster, paymasterInput: paymasterInput));
                var zkTx = await ThirdwebTransaction.ConvertToZkSyncTransaction(transaction);
                var zkTxSigned = await EIP712.GenerateSignature_ZkSyncTransaction("zkSync", "2", transaction.Input.ChainId.Value, zkTx, this);
                // Match bundler ZkTransactionInput type without recreating
                var hash = await this.ZkBroadcastTransaction(
                    new
                    {
                        nonce = zkTx.Nonce.ToString(),
                        from = zkTx.From,
                        to = zkTx.To,
                        gas = zkTx.GasLimit.ToString(),
                        gasPrice = string.Empty,
                        value = zkTx.Value.ToString(),
                        data = Utils.BytesToHex(zkTx.Data),
                        maxFeePerGas = zkTx.MaxFeePerGas.ToString(),
                        maxPriorityFeePerGas = zkTx.MaxPriorityFeePerGas.ToString(),
                        chainId = this._chainId.ToString(),
                        signedTransaction = zkTxSigned,
                        paymaster
                    }
                );
                return hash;
            }
            else
            {
                return await ThirdwebTransaction.Send(transaction);
            }
        }
        else
        {
            var signedOp = await this.SignUserOp(transactionInput);
            return await this.SendUserOp(signedOp);
        }
    }

    public async Task<ThirdwebTransactionReceipt> ExecuteTransaction(ThirdwebTransactionInput transactionInput)
    {
        var txHash = await this.SendTransaction(transactionInput);
        return await ThirdwebTransaction.WaitForTransactionReceipt(this.Client, this._chainId, txHash);
    }

    private async Task<(byte[] initCode, string factory, string factoryData)> GetInitCode()
    {
        if (await this.IsDeployed())
        {
            return (Array.Empty<byte>(), null, null);
        }

        var personalAccountAddress = await this._personalAccount.GetAddress();
        var factoryContract = new Contract(null, this._factoryContract.Abi, this._factoryContract.Address);
        var createFunction = factoryContract.GetFunction("createAccount");
        var data = createFunction.GetData(personalAccountAddress, Array.Empty<byte>());
        return (Utils.HexConcat(this._factoryContract.Address, data).HexToBytes(), this._factoryContract.Address, data);
    }

    private async Task<object> SignUserOp(ThirdwebTransactionInput transactionInput, int? requestId = null, bool simulation = false)
    {
        requestId ??= 1;

        (var initCode, var factory, var factoryData) = await this.GetInitCode();

        // Approve tokens if ERC20Paymaster
        if (this.UseERC20Paymaster && !this._isApproving && !this._isApproved && !simulation)
        {
            try
            {
                this._isApproving = true;
                var tokenContract = await ThirdwebContract.Create(this.Client, this._erc20PaymasterToken, this._chainId);
                var approvedAmount = await tokenContract.ERC20_Allowance(this._accountContract.Address, this._erc20PaymasterAddress);
                if (approvedAmount == 0)
                {
                    _ = await tokenContract.ERC20_Approve(this, this._erc20PaymasterAddress, BigInteger.Pow(2, 96) - 1);
                }
                this._isApproved = true;
                (initCode, factory, factoryData) = await this.GetInitCode();
            }
            catch (Exception e)
            {
                this._isApproved = false;
                throw new Exception($"Approving tokens for ERC20Paymaster spending failed: {e.Message}");
            }
            finally
            {
                this._isApproving = false;
            }
        }

        // Wait until deployed to avoid double initCode
        if (!simulation)
        {
            if (this.IsDeploying)
            {
                initCode = Array.Empty<byte>();
            }

            while (this.IsDeploying)
            {
                await Task.Delay(1000); // Wait for the deployment to finish
            }

            this.IsDeploying = initCode.Length > 0;
        }

        // Create the user operation and its safe (hexified) version

        var fees = await BundlerClient.ThirdwebGetUserOperationGasPrice(this.Client, this._bundlerUrl, requestId);
        var maxFee = new HexBigInteger(fees.MaxFeePerGas).Value;
        var maxPriorityFee = new HexBigInteger(fees.MaxPriorityFeePerGas).Value;

        var entryPointVersion = Utils.GetEntryPointVersion(this._entryPointContract.Address);

        if (entryPointVersion == 6)
        {
            var executeFn = new ExecuteFunction
            {
                Target = transactionInput.To,
                Value = transactionInput.Value.Value,
                Calldata = transactionInput.Data.HexToBytes(),
                FromAddress = await this.GetAddress(),
            };
            var executeInput = executeFn.CreateTransactionInput(await this.GetAddress());

            var partialUserOp = new UserOperationV6()
            {
                Sender = this._accountContract.Address,
                Nonce = await this.GetNonce(),
                InitCode = initCode,
                CallData = executeInput.Data.HexToBytes(),
                CallGasLimit = 0,
                VerificationGasLimit = 0,
                PreVerificationGas = 0,
                MaxFeePerGas = maxFee,
                MaxPriorityFeePerGas = maxPriorityFee,
                PaymasterAndData = Array.Empty<byte>(),
                Signature = Constants.DUMMY_SIG.HexToBytes(),
            };

            // Update paymaster data if any

            partialUserOp.PaymasterAndData = (await this.GetPaymasterAndData(requestId, EncodeUserOperation(partialUserOp), simulation)).PaymasterAndData.HexToBytes();

            // Estimate gas

            var gasEstimates = await BundlerClient.EthEstimateUserOperationGas(this.Client, this._bundlerUrl, requestId, EncodeUserOperation(partialUserOp), this._entryPointContract.Address);
            partialUserOp.CallGasLimit = 50000 + new HexBigInteger(gasEstimates.CallGasLimit).Value;
            partialUserOp.VerificationGasLimit = new HexBigInteger(gasEstimates.VerificationGasLimit).Value;
            partialUserOp.PreVerificationGas = new HexBigInteger(gasEstimates.PreVerificationGas).Value;

            // Update paymaster data if any

            partialUserOp.PaymasterAndData = (await this.GetPaymasterAndData(requestId, EncodeUserOperation(partialUserOp), simulation)).PaymasterAndData.HexToBytes();

            // Hash, sign and encode the user operation

            partialUserOp.Signature = await this.HashAndSignUserOp(partialUserOp, this._entryPointContract);

            return partialUserOp;
        }
        else
        {
            var executeFn = new ExecuteFunction
            {
                Target = transactionInput.To,
                Value = transactionInput.Value.Value,
                Calldata = transactionInput.Data.HexToBytes(),
                FromAddress = await this.GetAddress(),
            };
            var executeInput = executeFn.CreateTransactionInput(await this.GetAddress());

            var partialUserOp = new UserOperationV7()
            {
                Sender = this._accountContract.Address,
                Nonce = await this.GetNonce(),
                Factory = factory,
                FactoryData = factoryData.HexToBytes(),
                CallData = executeInput.Data.HexToBytes(),
                CallGasLimit = 0,
                VerificationGasLimit = 0,
                PreVerificationGas = 0,
                MaxFeePerGas = maxFee,
                MaxPriorityFeePerGas = maxPriorityFee,
                Paymaster = null,
                PaymasterVerificationGasLimit = 0,
                PaymasterPostOpGasLimit = 0,
                PaymasterData = Array.Empty<byte>(),
                Signature = Constants.DUMMY_SIG.HexToBytes(),
            };

            // Update Paymaster Data / Estimate gas

            if (this.UseERC20Paymaster && !this._isApproving)
            {
                var abiEncoder = new ABIEncode();
                var slotBytes = abiEncoder.GetABIEncoded(new ABIValue("address", this._accountContract.Address), new ABIValue("uint256", this._erc20PaymasterStorageSlot));
                var desiredBalance = BigInteger.Pow(2, 96) - 1;
                var storageDict = new Dictionary<string, string>
                {
                    { new Sha3Keccack().CalculateHash(slotBytes).BytesToHex().ToString(), desiredBalance.ToHexBigInteger().HexValue.HexToBytes32().BytesToHex() }
                };
                var stateDict = new Dictionary<string, object> { { this._erc20PaymasterToken, new { stateDiff = storageDict } } };
                var res = await this.GetPaymasterAndData(requestId, EncodeUserOperation(partialUserOp), simulation);
                partialUserOp.Paymaster = res.Paymaster;
                partialUserOp.PaymasterData = res.PaymasterData.HexToBytes();

                var gasEstimates = await BundlerClient.EthEstimateUserOperationGas(
                    this.Client,
                    this._bundlerUrl,
                    requestId,
                    EncodeUserOperation(partialUserOp),
                    this._entryPointContract.Address,
                    stateDict
                );
                partialUserOp.CallGasLimit = 21000 + new HexBigInteger(gasEstimates.CallGasLimit).Value;
                partialUserOp.VerificationGasLimit = new HexBigInteger(gasEstimates.VerificationGasLimit).Value;
                partialUserOp.PreVerificationGas = new HexBigInteger(gasEstimates.PreVerificationGas).Value;
                partialUserOp.PaymasterVerificationGasLimit = new HexBigInteger(gasEstimates.PaymasterVerificationGasLimit).Value;
                partialUserOp.PaymasterPostOpGasLimit = new HexBigInteger(gasEstimates.PaymasterPostOpGasLimit).Value;
            }
            else
            {
                var res = await this.GetPaymasterAndData(requestId, EncodeUserOperation(partialUserOp), simulation);
                partialUserOp.Paymaster = res.Paymaster;
                partialUserOp.PaymasterData = res.PaymasterData?.HexToBytes() ?? Array.Empty<byte>();
                partialUserOp.PreVerificationGas = new HexBigInteger(res.PreVerificationGas ?? "0").Value;
                partialUserOp.VerificationGasLimit = new HexBigInteger(res.VerificationGasLimit ?? "0").Value;
                partialUserOp.CallGasLimit = new HexBigInteger(res.CallGasLimit ?? "0").Value;
                partialUserOp.PaymasterVerificationGasLimit = new HexBigInteger(res.PaymasterVerificationGasLimit ?? "0").Value;
                partialUserOp.PaymasterPostOpGasLimit = new HexBigInteger(res.PaymasterPostOpGasLimit ?? "0").Value;
            }

            // Hash, sign and encode the user operation

            partialUserOp.Signature = await this.HashAndSignUserOp(partialUserOp, this._entryPointContract);

            return partialUserOp;
        }
    }

    private async Task<string> SendUserOp(object userOperation, int? requestId = null)
    {
        requestId ??= 1;

        // Encode op

        object encodedOp;
        if (userOperation is UserOperationV6)
        {
            encodedOp = EncodeUserOperation(userOperation as UserOperationV6);
        }
        else
        {
            encodedOp = userOperation is UserOperationV7 ? (object)EncodeUserOperation(userOperation as UserOperationV7) : throw new Exception("Invalid signed operation type");
        }

        // Send the user operation

        var userOpHash = await BundlerClient.EthSendUserOperation(this.Client, this._bundlerUrl, requestId, encodedOp, this._entryPointContract.Address);

        // Wait for the transaction to be mined

        string txHash = null;
        while (txHash == null)
        {
            var userOpReceipt = await BundlerClient.EthGetUserOperationReceipt(this.Client, this._bundlerUrl, requestId, userOpHash);
            txHash = userOpReceipt?.Receipt?.TransactionHash;
            await Task.Delay(1000).ConfigureAwait(false);
        }

        this.IsDeploying = false;
        return txHash;
    }

    private async Task<BigInteger> GetNonce()
    {
        var randomBytes = new byte[24];
        RandomNumberGenerator.Fill(randomBytes);
        BigInteger randomInt192 = new(randomBytes);
        randomInt192 = BigInteger.Abs(randomInt192) % (BigInteger.One << 192);
        return await ThirdwebContract.Read<BigInteger>(this._entryPointContract, "getNonce", await this.GetAddress(), randomInt192);
    }

    private async Task<(string, string)> ZkPaymasterData(ThirdwebTransactionInput transactionInput)
    {
        if (this._gasless)
        {
            var result = await BundlerClient.ZkPaymasterData(this.Client, this._paymasterUrl, 1, transactionInput);
            return (result.Paymaster, result.PaymasterInput);
        }
        else
        {
            return (null, null);
        }
    }

    private async Task<string> ZkBroadcastTransaction(object transactionInput)
    {
        var result = await BundlerClient.ZkBroadcastTransaction(this.Client, this._bundlerUrl, 1, transactionInput);
        return result.TransactionHash;
    }

    private async Task<PMSponsorOperationResponse> GetPaymasterAndData(object requestId, object userOp, bool simulation)
    {
        if (this.UseERC20Paymaster && !this._isApproving && !simulation)
        {
            return new PMSponsorOperationResponse()
            {
                PaymasterAndData = Utils.HexConcat(this._erc20PaymasterAddress, this._erc20PaymasterToken),
                Paymaster = this._erc20PaymasterAddress,
                PaymasterData = "0x",
            };
        }
        else
        {
            return this._gasless ? await BundlerClient.PMSponsorUserOperation(this.Client, this._paymasterUrl, requestId, userOp, this._entryPointContract.Address) : new PMSponsorOperationResponse();
        }
    }

    private async Task<byte[]> HashAndSignUserOp(UserOperationV6 userOp, ThirdwebContract entryPointContract)
    {
        var userOpHash = await ThirdwebContract.Read<byte[]>(entryPointContract, "getUserOpHash", userOp);
        var sig =
            this._personalAccount.AccountType == ThirdwebAccountType.ExternalAccount
                ? await this._personalAccount.PersonalSign(userOpHash.BytesToHex())
                : await this._personalAccount.PersonalSign(userOpHash);
        return sig.HexToBytes();
    }

    private async Task<byte[]> HashAndSignUserOp(UserOperationV7 userOp, ThirdwebContract entryPointContract)
    {
        var factoryBytes = userOp.Factory.HexToBytes();
        var factoryDataBytes = userOp.FactoryData;
        var initCodeBuffer = new byte[factoryBytes.Length + factoryDataBytes.Length];
        Buffer.BlockCopy(factoryBytes, 0, initCodeBuffer, 0, factoryBytes.Length);
        Buffer.BlockCopy(factoryDataBytes, 0, initCodeBuffer, factoryBytes.Length, factoryDataBytes.Length);

        var verificationGasLimitBytes = userOp.VerificationGasLimit.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var callGasLimitBytes = userOp.CallGasLimit.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var accountGasLimitsBuffer = new byte[32];
        Buffer.BlockCopy(verificationGasLimitBytes, 0, accountGasLimitsBuffer, 0, 16);
        Buffer.BlockCopy(callGasLimitBytes, 0, accountGasLimitsBuffer, 16, 16);

        var maxPriorityFeePerGasBytes = userOp.MaxPriorityFeePerGas.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var maxFeePerGasBytes = userOp.MaxFeePerGas.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var gasFeesBuffer = new byte[32];
        Buffer.BlockCopy(maxPriorityFeePerGasBytes, 0, gasFeesBuffer, 0, 16);
        Buffer.BlockCopy(maxFeePerGasBytes, 0, gasFeesBuffer, 16, 16);

        var paymasterBytes = userOp.Paymaster.HexToBytes();
        var paymasterVerificationGasLimitBytes = userOp.PaymasterVerificationGasLimit.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var paymasterPostOpGasLimitBytes = userOp.PaymasterPostOpGasLimit.ToHexBigInteger().HexValue.HexToBytes().PadBytes(16);
        var paymasterDataBytes = userOp.PaymasterData;
        var paymasterAndDataBuffer = new byte[20 + 16 + 16 + paymasterDataBytes.Length];
        Buffer.BlockCopy(paymasterBytes, 0, paymasterAndDataBuffer, 0, 20);
        Buffer.BlockCopy(paymasterVerificationGasLimitBytes, 0, paymasterAndDataBuffer, 20, 16);
        Buffer.BlockCopy(paymasterPostOpGasLimitBytes, 0, paymasterAndDataBuffer, 20 + 16, 16);
        Buffer.BlockCopy(paymasterDataBytes, 0, paymasterAndDataBuffer, 20 + 16 + 16, paymasterDataBytes.Length);

        var packedOp = new PackedUserOperation()
        {
            Sender = userOp.Sender,
            Nonce = userOp.Nonce,
            InitCode = initCodeBuffer,
            CallData = userOp.CallData,
            AccountGasLimits = accountGasLimitsBuffer,
            PreVerificationGas = userOp.PreVerificationGas,
            GasFees = gasFeesBuffer,
            PaymasterAndData = paymasterAndDataBuffer,
            Signature = userOp.Signature
        };

        var userOpHash = await ThirdwebContract.Read<byte[]>(entryPointContract, "getUserOpHash", packedOp);

        var sig =
            this._personalAccount.AccountType == ThirdwebAccountType.ExternalAccount
                ? await this._personalAccount.PersonalSign(userOpHash.BytesToHex())
                : await this._personalAccount.PersonalSign(userOpHash);

        return sig.HexToBytes();
    }

    private static UserOperationHexifiedV6 EncodeUserOperation(UserOperationV6 userOperation)
    {
        return new UserOperationHexifiedV6()
        {
            sender = userOperation.Sender,
            nonce = userOperation.Nonce.ToHexBigInteger().HexValue,
            initCode = userOperation.InitCode.BytesToHex(),
            callData = userOperation.CallData.BytesToHex(),
            callGasLimit = userOperation.CallGasLimit.ToHexBigInteger().HexValue,
            verificationGasLimit = userOperation.VerificationGasLimit.ToHexBigInteger().HexValue,
            preVerificationGas = userOperation.PreVerificationGas.ToHexBigInteger().HexValue,
            maxFeePerGas = userOperation.MaxFeePerGas.ToHexBigInteger().HexValue,
            maxPriorityFeePerGas = userOperation.MaxPriorityFeePerGas.ToHexBigInteger().HexValue,
            paymasterAndData = userOperation.PaymasterAndData.BytesToHex(),
            signature = userOperation.Signature.BytesToHex()
        };
    }

    private static UserOperationHexifiedV7 EncodeUserOperation(UserOperationV7 userOperation)
    {
        return new UserOperationHexifiedV7()
        {
            sender = userOperation.Sender,
            nonce = Utils.HexConcat(Constants.ADDRESS_ZERO, userOperation.Nonce.ToHexBigInteger().HexValue),
            factory = userOperation.Factory,
            factoryData = userOperation.FactoryData.BytesToHex(),
            callData = userOperation.CallData.BytesToHex(),
            callGasLimit = userOperation.CallGasLimit.ToHexBigInteger().HexValue,
            verificationGasLimit = userOperation.VerificationGasLimit.ToHexBigInteger().HexValue,
            preVerificationGas = userOperation.PreVerificationGas.ToHexBigInteger().HexValue,
            maxFeePerGas = userOperation.MaxFeePerGas.ToHexBigInteger().HexValue,
            maxPriorityFeePerGas = userOperation.MaxPriorityFeePerGas.ToHexBigInteger().HexValue,
            paymaster = userOperation.Paymaster,
            paymasterVerificationGasLimit = userOperation.PaymasterVerificationGasLimit.ToHexBigInteger().HexValue,
            paymasterPostOpGasLimit = userOperation.PaymasterPostOpGasLimit.ToHexBigInteger().HexValue,
            paymasterData = userOperation.PaymasterData.BytesToHex(),
            signature = userOperation.Signature.BytesToHex()
        };
    }

    public async Task ForceDeploy()
    {
        if (Utils.IsZkSync(this._chainId))
        {
            return;
        }

        if (await this.IsDeployed())
        {
            return;
        }

        if (this.IsDeploying)
        {
            throw new InvalidOperationException("SmartAccount.ForceDeploy: Account is already deploying.");
        }

        var input = new ThirdwebTransactionInput()
        {
            Data = "0x",
            To = this._accountContract.Address,
            Value = new HexBigInteger(0)
        };
        var txHash = await this.SendTransaction(input);
        _ = await ThirdwebTransaction.WaitForTransactionReceipt(this.Client, this._chainId, txHash);
    }

    public Task<IThirdwebWallet> GetPersonalWallet()
    {
        return Task.FromResult(this._personalAccount);
    }

    public async Task<string> GetAddress()
    {
        return Utils.IsZkSync(this._chainId) ? await this._personalAccount.GetAddress() : this._accountContract.Address.ToChecksumAddress();
    }

    public Task<string> EthSign(byte[] rawMessage)
    {
        throw new NotImplementedException();
    }

    public Task<string> EthSign(string message)
    {
        throw new NotImplementedException();
    }

    public Task<string> RecoverAddressFromEthSign(string message, string signature)
    {
        throw new NotImplementedException();
    }

    public Task<string> PersonalSign(byte[] rawMessage)
    {
        throw new NotImplementedException();
    }

    public async Task<string> PersonalSign(string message)
    {
        if (Utils.IsZkSync(this._chainId))
        {
            return await this._personalAccount.PersonalSign(message);
        }

        if (!await this.IsDeployed())
        {
            while (this.IsDeploying)
            {
                await Task.Delay(1000); // Wait for the deployment to finish
            }
            await this.ForceDeploy();
        }

        if (await this.IsDeployed())
        {
            var originalMsgHash = Encoding.UTF8.GetBytes(message).HashPrefixedMessage();
            bool factorySupports712;
            try
            {
                _ = await ThirdwebContract.Read<byte[]>(this._accountContract, "getMessageHash", originalMsgHash);
                factorySupports712 = true;
            }
            catch
            {
                factorySupports712 = false;
            }

            var sig = factorySupports712
                ? await EIP712.GenerateSignature_SmartAccount_AccountMessage("Account", "1", this._chainId, await this.GetAddress(), originalMsgHash, this._personalAccount)
                : await this._personalAccount.PersonalSign(message);

            var isValid = await this.IsValidSignature(message, sig);
            return isValid ? sig : throw new Exception("Invalid signature.");
        }
        else
        {
            throw new Exception("Smart account could not be deployed, unable to sign message.");
        }
    }

    public async Task<string> RecoverAddressFromPersonalSign(string message, string signature)
    {
        return !await this.IsValidSignature(message, signature) ? await this._personalAccount.RecoverAddressFromPersonalSign(message, signature) : await this.GetAddress();
    }

    public async Task<bool> IsValidSignature(string message, string signature)
    {
        try
        {
            var magicValue = await ThirdwebContract.Read<byte[]>(this._accountContract, "isValidSignature", message.StringToHex(), signature.HexToBytes());
            return magicValue.BytesToHex() == new byte[] { 0x16, 0x26, 0xba, 0x7e }.BytesToHex();
        }
        catch
        {
            try
            {
                var magicValue = await ThirdwebContract.Read<byte[]>(this._accountContract, "isValidSignature", Encoding.UTF8.GetBytes(message).HashPrefixedMessage(), signature.HexToBytes());
                return magicValue.BytesToHex() == new byte[] { 0x16, 0x26, 0xba, 0x7e }.BytesToHex();
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<List<string>> GetAllAdmins()
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new InvalidOperationException("Account Permissions are not supported in ZkSync");
        }

        var result = await ThirdwebContract.Read<List<string>>(this._accountContract, "getAllAdmins");
        return result ?? new List<string>();
    }

    public async Task<List<SignerPermissions>> GetAllActiveSigners()
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new InvalidOperationException("Account Permissions are not supported in ZkSync");
        }

        var result = await ThirdwebContract.Read<List<SignerPermissions>>(this._accountContract, "getAllActiveSigners");
        return result ?? new List<SignerPermissions>();
    }

    public async Task<ThirdwebTransactionReceipt> CreateSessionKey(
        string signerAddress,
        List<string> approvedTargets,
        string nativeTokenLimitPerTransactionInWei,
        string permissionStartTimestamp,
        string permissionEndTimestamp,
        string reqValidityStartTimestamp,
        string reqValidityEndTimestamp
    )
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new InvalidOperationException("Account Permissions are not supported in ZkSync");
        }

        var request = new SignerPermissionRequest()
        {
            Signer = signerAddress,
            IsAdmin = 0,
            ApprovedTargets = approvedTargets,
            NativeTokenLimitPerTransaction = BigInteger.Parse(nativeTokenLimitPerTransactionInWei),
            PermissionStartTimestamp = BigInteger.Parse(permissionStartTimestamp),
            PermissionEndTimestamp = BigInteger.Parse(permissionEndTimestamp),
            ReqValidityStartTimestamp = BigInteger.Parse(reqValidityStartTimestamp),
            ReqValidityEndTimestamp = BigInteger.Parse(reqValidityEndTimestamp),
            Uid = Guid.NewGuid().ToByteArray()
        };

        var signature = await EIP712.GenerateSignature_SmartAccount("Account", "1", this._chainId, await this.GetAddress(), request, this._personalAccount);
        // Do it this way to avoid triggering an extra sig from estimation
        var data = new Contract(null, this._accountContract.Abi, this._accountContract.Address).GetFunction("setPermissionsForSigner").GetData(request, signature.HexToBytes());
        var txInput = new ThirdwebTransactionInput()
        {
            To = this._accountContract.Address,
            Value = new HexBigInteger(0),
            Data = data
        };
        var txHash = await this.SendTransaction(txInput);
        return await ThirdwebTransaction.WaitForTransactionReceipt(this.Client, this._chainId, txHash);
    }

    public async Task<ThirdwebTransactionReceipt> RevokeSessionKey(string signerAddress)
    {
        return Utils.IsZkSync(this._chainId)
            ? throw new InvalidOperationException("Account Permissions are not supported in ZkSync")
            : await this.CreateSessionKey(signerAddress, new List<string>(), "0", "0", "0", "0", Utils.GetUnixTimeStampIn10Years().ToString());
    }

    public async Task<ThirdwebTransactionReceipt> AddAdmin(string admin)
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new InvalidOperationException("Account Permissions are not supported in ZkSync");
        }

        var request = new SignerPermissionRequest()
        {
            Signer = admin,
            IsAdmin = 1,
            ApprovedTargets = new List<string>(),
            NativeTokenLimitPerTransaction = 0,
            PermissionStartTimestamp = Utils.GetUnixTimeStampNow() - 3600,
            PermissionEndTimestamp = Utils.GetUnixTimeStampIn10Years(),
            ReqValidityStartTimestamp = Utils.GetUnixTimeStampNow() - 3600,
            ReqValidityEndTimestamp = Utils.GetUnixTimeStampIn10Years(),
            Uid = Guid.NewGuid().ToByteArray()
        };

        var signature = await EIP712.GenerateSignature_SmartAccount("Account", "1", this._chainId, await this.GetAddress(), request, this._personalAccount);
        var data = new Contract(null, this._accountContract.Abi, this._accountContract.Address).GetFunction("setPermissionsForSigner").GetData(request, signature.HexToBytes());
        var txInput = new ThirdwebTransactionInput()
        {
            To = this._accountContract.Address,
            Value = new HexBigInteger(0),
            Data = data
        };
        var txHash = await this.SendTransaction(txInput);
        return await ThirdwebTransaction.WaitForTransactionReceipt(this.Client, this._chainId, txHash);
    }

    public async Task<ThirdwebTransactionReceipt> RemoveAdmin(string admin)
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new InvalidOperationException("Account Permissions are not supported in ZkSync");
        }

        var request = new SignerPermissionRequest()
        {
            Signer = admin,
            IsAdmin = 2,
            ApprovedTargets = new List<string>(),
            NativeTokenLimitPerTransaction = 0,
            PermissionStartTimestamp = Utils.GetUnixTimeStampNow() - 3600,
            PermissionEndTimestamp = Utils.GetUnixTimeStampIn10Years(),
            ReqValidityStartTimestamp = Utils.GetUnixTimeStampNow() - 3600,
            ReqValidityEndTimestamp = Utils.GetUnixTimeStampIn10Years(),
            Uid = Guid.NewGuid().ToByteArray()
        };

        var signature = await EIP712.GenerateSignature_SmartAccount("Account", "1", this._chainId, await this.GetAddress(), request, this._personalAccount);
        var data = new Contract(null, this._accountContract.Abi, this._accountContract.Address).GetFunction("setPermissionsForSigner").GetData(request, signature.HexToBytes());
        var txInput = new ThirdwebTransactionInput()
        {
            To = this._accountContract.Address,
            Value = new HexBigInteger(0),
            Data = data
        };
        var txHash = await this.SendTransaction(txInput);
        return await ThirdwebTransaction.WaitForTransactionReceipt(this.Client, this._chainId, txHash);
    }

    public Task<string> SignTypedDataV4(string json)
    {
        return this._personalAccount.SignTypedDataV4(json);
    }

    public Task<string> SignTypedDataV4<T, TDomain>(T data, TypedData<TDomain> typedData)
        where TDomain : IDomain
    {
        return this._personalAccount.SignTypedDataV4(data, typedData);
    }

    public Task<string> RecoverAddressFromTypedDataV4<T, TDomain>(T data, TypedData<TDomain> typedData, string signature)
        where TDomain : IDomain
    {
        return this._personalAccount.RecoverAddressFromTypedDataV4(data, typedData, signature);
    }

    public async Task<BigInteger> EstimateUserOperationGas(ThirdwebTransactionInput transaction)
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new Exception("User Operations are not supported in ZkSync");
        }

        var signedOp = await this.SignUserOp(transaction, null, simulation: true);
        if (signedOp is UserOperationV6)
        {
            var castSignedOp = signedOp as UserOperationV6;
            var cost = castSignedOp.CallGasLimit + castSignedOp.VerificationGasLimit + castSignedOp.PreVerificationGas;
            return cost;
        }
        else if (signedOp is UserOperationV7)
        {
            var castSignedOp = signedOp as UserOperationV7;
            var cost =
                castSignedOp.CallGasLimit + castSignedOp.VerificationGasLimit + castSignedOp.PreVerificationGas + castSignedOp.PaymasterVerificationGasLimit + castSignedOp.PaymasterPostOpGasLimit;
            return cost;
        }
        else
        {
            throw new Exception("Invalid signed operation type");
        }
    }

    public async Task<string> SignTransaction(ThirdwebTransactionInput transaction)
    {
        if (Utils.IsZkSync(this._chainId))
        {
            throw new Exception("Offline Signing is not supported in ZkSync");
        }

        var signedOp = await this.SignUserOp(transaction);
        if (signedOp is UserOperationV6)
        {
            var encodedOp = EncodeUserOperation(signedOp as UserOperationV6);
            return JsonConvert.SerializeObject(encodedOp);
        }
        else if (signedOp is UserOperationV7)
        {
            var encodedOp = EncodeUserOperation(signedOp as UserOperationV7);
            return JsonConvert.SerializeObject(encodedOp);
        }
        else
        {
            throw new Exception("Invalid signed operation type");
        }
    }

    public async Task<bool> IsConnected()
    {
        return Utils.IsZkSync(this._chainId) ? await this._personalAccount.IsConnected() : this._accountContract != null;
    }

    public Task Disconnect()
    {
        this._accountContract = null;
        return Task.CompletedTask;
    }
}
