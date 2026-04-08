using SolarApp.Models.Enums;
using SolarApp.Services.InverterApi.Adapters;

namespace SolarApp.Services.InverterApi;

public class InverterApiAdapterFactory : IInverterApiAdapterFactory
{
    private readonly IServiceProvider _serviceProvider;

    public InverterApiAdapterFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IInverterApiAdapter GetAdapter(InverterBrand brand)
    {
        return brand switch
        {
            InverterBrand.Huawei => _serviceProvider.GetRequiredService<HuaweiAdapter>(),
            InverterBrand.FoxESS => _serviceProvider.GetRequiredService<FoxEssAdapter>(),
            InverterBrand.Solis => _serviceProvider.GetRequiredService<SolisAdapter>(),
            InverterBrand.GoodWe => _serviceProvider.GetRequiredService<GoodWeAdapter>(),
            InverterBrand.Deye => _serviceProvider.GetRequiredService<DeyeAdapter>(),
            _ => throw new NotSupportedException($"Inverter brand '{brand}' is not supported yet.")
        };
    }
}
