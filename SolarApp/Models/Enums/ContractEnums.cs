namespace SolarApp.Models.Enums;

public enum ContractStatus
{
    Draft = 0,
    Active = 1,
    /// <summary>Ran to its end date and was not renewed.</summary>
    Expired = 2,
    /// <summary>Ended early — by either side, before the end date.</summary>
    Terminated = 3,
    /// <summary>Paused: still ours, temporarily not being serviced or billed.</summary>
    Suspended = 4
}

public enum ContractBillingCycle
{
    Monthly = 1,
    Quarterly = 2,
    Yearly = 3,
    /// <summary>A single fee for the whole term — excluded from recurring-revenue totals.</summary>
    OneTime = 4
}
