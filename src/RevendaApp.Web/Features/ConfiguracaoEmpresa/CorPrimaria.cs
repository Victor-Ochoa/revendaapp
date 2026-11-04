using MudBlazor.Utilities;

namespace RevendaApp.Web.Features.ConfiguracaoEmpresa;

/// <summary>
/// Converte a cor primária entre o que o <b>validador do comando</b> aceita e o que o
/// <see cref="MudBlazor.MudColorPicker"/> manipula.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que existe:</b> o validador (<see cref="EditarConfigEmpresaCommandValidator"/>) aceita
/// <b>apenas</b> <c>^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$</c> — 3 ou 6 dígitos. O picker trabalha com
/// <see cref="MudColor"/>, que carrega alfa (8 dígitos). Entregar o hex com alfa ao comando faria
/// <b>todo salvamento com cor escolhida falhar na validação</b>.
/// </para>
/// <para>
/// Fica fora do <c>.razor</c> de propósito: assim a conversão é provada por teste unitário, sem
/// precisar renderizar Blazor.
/// </para>
/// </remarks>
internal static class CorPrimaria
{
    /// <summary>Cor assumida quando a empresa ainda não escolheu uma (azul marinho do tema).</summary>
    internal const string Padrao = "#0A1F44";

    /// <summary>
    /// Formata um <see cref="MudColor"/> no hex de 6 dígitos que o validador aceita.
    /// <see cref="MudColorOutputFormats.Hex"/> já omite o alfa (ex.: <c>#0a1f44</c>) — não é preciso
    /// fatiar a string.
    /// </summary>
    internal static string ParaHex(MudColor cor) => cor.ToString(MudColorOutputFormats.Hex);

    /// <summary>
    /// Lê a cor gravada no banco; devolve a padrão quando nula/inválida. <b>Nunca lança</b>: o valor
    /// vem do banco e pode estar vazio ou legado, e um throw aqui derrubaria o circuito ao abrir a aba.
    /// </summary>
    internal static MudColor ParaPicker(string? hex)
        => MudColor.TryParse(hex, out var cor) ? cor : new MudColor(Padrao);
}