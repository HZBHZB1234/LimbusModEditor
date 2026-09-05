using LimbusModEditor.Formats.Abstractions;
using LimbusModEditor.Formats.Bank;
using LimbusModEditor.Formats.Carra;
using LimbusModEditor.Formats.Lunartique;
using LimbusModEditor.Formats.Rebank;

namespace LimbusModEditor.Application.Formats;

public static class BuiltInFormatRegistry
{
    public static FormatRegistry Create() => new FormatRegistry([
        new CarraFormatHandler(),
        new LunartiqueFormatHandler(),
        new BankFormatHandler(),
        new RebankFormatHandler()
    ]);
}
