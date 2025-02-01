using System;
using System.Collections.Generic;
using System.Linq;
using Avalara.AvaTax.RestClient;
using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Tax.Avalara.Domain;
using Nop.Services.Logging;

namespace Nop.Plugin.Tax.Avalara.Services
{
    /// <summary>
    /// Represents the manager that operates with requests to the Avalara services
    /// </summary>
    public class AvalaraTaxManager
    {
        #region Fields

        private readonly AvalaraTaxSettings _avalaraTaxSettings;
        private readonly ILogger _logger;
        private readonly ITaxTransactionLogService _taxTransactionLogService;
        private readonly IWorkContext _workContext;

        #endregion

        #region Ctor

        public AvalaraTaxManager(AvalaraTaxSettings avalaraTaxSettings,
            ILogger logger,
            ITaxTransactionLogService taxTransactionLogService,
            IWorkContext workContext)
        {
            this._avalaraTaxSettings = avalaraTaxSettings;
            this._logger = logger;
            this._taxTransactionLogService = taxTransactionLogService;
            this._workContext = workContext;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Check that tax provider is configured
        /// </summary>
        /// <returns>True if it's configured; otherwise false</returns>
        private bool IsConfigured()
        {
            return !string.IsNullOrEmpty(_avalaraTaxSettings.AccountId)
                && !string.IsNullOrEmpty(_avalaraTaxSettings.LicenseKey);
        }

        /// <summary>
        /// Create client that connects to Avalara services
        /// </summary>
        /// <returns>Avalara client</returns>
        private AvaTaxClient CreateServiceClient()
        {
            //create a client
            var serviceClient = new AvaTaxClient(AvalaraTaxDefaults.ApplicationName,
                AvalaraTaxDefaults.ApplicationVersion,
                Environment.MachineName,
                _avalaraTaxSettings.UseSandbox ? AvaTaxEnvironment.Sandbox : AvaTaxEnvironment.Production);

            //use credentials
            serviceClient.WithSecurity(_avalaraTaxSettings.AccountId, _avalaraTaxSettings.LicenseKey);

            return serviceClient;
        }

        /// <summary>
        /// Handle request
        /// </summary>
        /// <typeparam name="T">Output type</typeparam>
        /// <param name="request">Request actions</param>
        /// <returns>Object of T type</returns>
        private T HandleRequest<T>(Func<T> request)
        {
            try
            {
                //ensure that Avalara tax provider is configured
                if (!IsConfigured())
                    throw new NopException("Tax provider is not configured");

                return request();
            }
            catch (Exception exception)
            {
                //compose an error message
                var errorMessage = exception.Message;
                if (exception is AvaTaxError avaTaxError && avaTaxError.error != null)
                {
                    //log tax transaction error
                    LogTaxTransaction(avaTaxError.error, LogType.Error);

                    var errorInfo = avaTaxError.error.error;
                    if (errorInfo != null)
                    {
                        errorMessage = $"{errorInfo.code} - {errorInfo.message}{Environment.NewLine}";
                        if (errorInfo.details?.Any() ?? false)
                        {
                            var errorDetails = errorInfo.details.Aggregate(string.Empty, (error, detail) => $"{error}{detail.description}{Environment.NewLine}");
                            errorMessage = $"{errorMessage} Details: {errorDetails}";
                        }
                    }
                }

                //log errors
                _logger.Error($"Avalara tax provider error. {errorMessage}", exception, _workContext.CurrentCustomer);

                return default(T);
            }
        }

        /// <summary>
        /// Log tax transaction details
        /// </summary>
        /// <typeparam name="T">Type of value</typeparam>
        /// <param name="value">Value</param>
        /// <param name="logType">Log type</param>
        private void LogTaxTransaction<T>(T value, LogType logType)
        {
            _taxTransactionLogService.InsertTaxTransactionLog(new TaxTransactionLog
            {
                LogType = logType,
                Message = value.ToString(),
                CustomerId = _workContext.CurrentCustomer.Id,
                CreatedDateUtc = DateTime.UtcNow
            });
        }

        #endregion

        #region Methods

        /// <summary>
        /// Ping service (test conection)
        /// </summary>
        /// <returns>Ping result</returns>
        public PingResultModel Ping()
        {
            return HandleRequest(() => CreateServiceClient().Ping() ?? throw new NopException("No response from the service"));
        }

        /// <summary>
        /// Get all companies of the account
        /// </summary>
        /// <param name="activeOnly">Whether to find only active companies</param>
        /// <returns>List of companies</returns>
        public List<CompanyModel> GetAccountCompanies(bool activeOnly)
        {
            return HandleRequest(() =>
            {
                //create filter
                var filter = activeOnly ? "isActive eq true" : null;

                //get result
                var result = CreateServiceClient().QueryCompanies(null, filter, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the paginated and filtered list
                return result.value;
            });
        }

        /// <summary>
        /// Get Avalara pre-defined entity use codes
        /// </summary>
        /// <returns>List of entity use codes</returns>
        public List<EntityUseCodeModel> GetEntityUseCodes()
        {
            return HandleRequest(() =>
            {
                //get result
                var result = CreateServiceClient().ListEntityUseCodes(null, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the paginated and filtered list
                return result.value;
            });
        }

        /// <summary>
        /// Get Avalara pre-defined tax code types
        /// </summary>
        /// <returns>Key-value pairs of tax code types</returns>
        public Dictionary<string, string> GetTaxCodeTypes()
        {
            return HandleRequest(() =>
            {
                //get result
                var result = CreateServiceClient().ListTaxCodeTypes(null, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the list of tax code types
                return result.types;
            });
        }

        /// <summary>
        /// Get Avalara system tax codes
        /// </summary>
        /// <param name="activeOnly">Whether to find only active tax codes</param>
        /// <returns>List of tax codes</returns>
        public List<TaxCodeModel> GetSystemTaxCodes(bool activeOnly)
        {
            return HandleRequest(() =>
            {
                //create filter
                var filter = activeOnly ? "isActive eq true" : null;

                //get result
                var result = CreateServiceClient().ListTaxCodes(filter, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the paginated and filtered list
                return result.value;
            });
        }

        /// <summary>
        /// Get tax codes of the selected company
        /// </summary>
        /// <param name="activeOnly">Whether to find only active tax codes</param>
        /// <returns>List of tax codes</returns>
        public List<TaxCodeModel> GetTaxCodes(bool activeOnly)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //get selected company
                var selectedCompany = GetAccountCompanies(true)
                    ?.FirstOrDefault(company => _avalaraTaxSettings.CompanyCode.Equals(company?.companyCode))
                    ?? throw new NopException("Failed to retrieve company");

                //create filter
                var filter = activeOnly ? "isActive eq true" : null;

                //get result
                var result = CreateServiceClient().ListTaxCodesByCompany(selectedCompany.id, filter, null, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the paginated and filtered list
                return result.value;
            });
        }

        /// <summary>
        /// Create custom tax codes for the selected company
        /// </summary>
        /// <param name="taxCodeModels">Tax codes</param>
        /// <returns>List of tax codes</returns>
        public List<TaxCodeModel> CreateTaxCodes(List<TaxCodeModel> taxCodeModels)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //get selected company
                var selectedCompany = GetAccountCompanies(true)
                    ?.FirstOrDefault(company => _avalaraTaxSettings.CompanyCode.Equals(company?.companyCode))
                    ?? throw new NopException("Failed to retrieve company");

                //create tax codes and return the result
                return CreateServiceClient().CreateTaxCodes(selectedCompany.id, taxCodeModels)
                    ?? throw new NopException("No response from the service");
            });
        }

        /// <summary>
        /// Get company items
        /// </summary>
        /// <returns>List of items</returns>
        public List<ItemModel> GetItems()
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //get selected company
                var selectedCompany = GetAccountCompanies(true)
                    ?.FirstOrDefault(company => _avalaraTaxSettings.CompanyCode.Equals(company?.companyCode))
                    ?? throw new NopException("Failed to retrieve company");

                //get result
                var result = CreateServiceClient().ListItemsByCompany(selectedCompany.id, null, null, null, null, null)
                    ?? throw new NopException("No response from the service");

                //return the paginated and filtered list
                return result.value;
            });
        }

        /// <summary>
        /// Create items for the selected company
        /// </summary>
        /// <param name="itemModels">Items</param>
        /// <returns>List of items</returns>
        public List<ItemModel> CreateItems(List<ItemModel> itemModels)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //get selected company
                var selectedCompany = GetAccountCompanies(true)
                    ?.FirstOrDefault(company => _avalaraTaxSettings.CompanyCode.Equals(company?.companyCode))
                    ?? throw new NopException("Failed to retrieve company");

                //create items and return the result
                return CreateServiceClient().CreateItems(selectedCompany.id, itemModels)
                    ?? throw new NopException("No response from the service");
            });
        }

        /// <summary>
        /// Get tax transaction by code and type
        /// </summary>
        /// <param name="transactionCode">Transaction code</param>
        /// <param name="type">Transaction type</param>
        /// <returns>Transaction</returns>
        public TransactionModel GetTransaction(string transactionCode, DocumentType type = DocumentType.SalesInvoice)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //return result
                return CreateServiceClient().GetTransactionByCodeAndType(_avalaraTaxSettings.CompanyCode, transactionCode, type, null)
                    ?? throw new NopException("No response from the service");
            });
        }

        /// <summary>
        /// Create tax transaction
        /// </summary>
        /// <param name="createTransactionModel">Request parameters to create a transaction</param>
        /// <param name="logTransactionDetails">Whether to log tax transaction request and response</param>
        /// <returns>Transaction</returns>
        public TransactionModel CreateTaxTransaction(CreateTransactionModel createTransactionModel, bool logTransactionDetails)
        {
            return HandleRequest(() =>
            {
                //log tax transaction request
                if (logTransactionDetails)
                    LogTaxTransaction(createTransactionModel, LogType.Create);

                //create transaction
                var transaction = CreateServiceClient().CreateTransaction(string.Empty, createTransactionModel)
                    ?? throw new NopException("No response from the service");

                //log tax transaction response
                if (logTransactionDetails)
                    LogTaxTransaction(transaction, LogType.CreateResponse);

                //whether there are any errors
                var errors = transaction.messages?.Where(m => !m.severity?.ToLower().Equals("success") ?? true).ToList() ?? new List<AvaTaxMessage>();

                if (!errors.Any())
                    return transaction;
                
                throw new NopException(errors.Aggregate(string.Empty, (error, message) => $"{error}{message.summary}{Environment.NewLine}"));
            });
        }

        /// <summary>
        /// Void tax transaction
        /// </summary>
        /// <param name="voidTransactionModel">Request parameters to void a transaction</param>
        /// <param name="transactionCode">Transaction code</param>
        /// <returns>Transaction</returns>
        public TransactionModel VoidTax(VoidTransactionModel voidTransactionModel, string transactionCode)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //log tax transaction request
                LogTaxTransaction(voidTransactionModel, LogType.Void);

                //return result
                var transaction = CreateServiceClient().VoidTransaction(_avalaraTaxSettings.CompanyCode, transactionCode, voidTransactionModel)
                    ?? throw new NopException("No response from the service");

                //log tax transaction response
                LogTaxTransaction(transaction, LogType.VoidResponse);

                return transaction;
            });
        }

        /// <summary>
        /// Refund tax transaction
        /// </summary>
        /// <param name="refundTransactionModel">Request parameters to refund a transaction</param>
        /// <param name="transactionCode">Transaction code</param>
        /// <returns>Transaction</returns>
        public TransactionModel RefundTax(RefundTransactionModel refundTransactionModel, string transactionCode)
        {
            return HandleRequest(() =>
            {
                if (string.IsNullOrEmpty(_avalaraTaxSettings.CompanyCode) || _avalaraTaxSettings.CompanyCode.Equals(Guid.Empty.ToString()))
                    throw new NopException("Company not selected");

                //log tax transaction request
                LogTaxTransaction(refundTransactionModel, LogType.Refund);

                //return result
                var transaction = CreateServiceClient().RefundTransaction(_avalaraTaxSettings.CompanyCode, transactionCode, null, refundTransactionModel)
                    ?? throw new NopException("No response from the service");

                //log tax transaction response
                LogTaxTransaction(transaction, LogType.RefundResponse);

                return transaction;
            });
        }

        /// <summary>
        /// Resolve the passed address against Avalara's address-validation system
        /// </summary>
        /// <param name="addressToValidate">Address to validate</param>
        /// <returns>Validated address</returns>
        public AddressResolutionModel ValidateAddress(AddressValidationInfo addressToValidate)
        {
            return HandleRequest(() =>
            {
                //return result
                return CreateServiceClient().ResolveAddressPost(addressToValidate)
                    ?? throw new NopException("No response from the service");
            });
        }

        #endregion
    }
}