// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/evalhelpers/formatspecifier.h"
#include <string_view>
#include <unordered_map>

namespace dncdbg::EvalFormatSpecifiers
{

void ParseFormatSpecifier(const std::string &expressionWithFormat, std::string &expression, FormatSpecifier &specifier)
{
    // Format specifiers
    // https://learn.microsoft.com/en-us/visualstudio/debugger/format-specifiers-in-csharp?view=visualstudio
    static const std::unordered_map<std::string_view, FormatSpecifier> formatMap{
        {"ac",      FormatSpecifier::ForceEvaluation},
        {"d",       FormatSpecifier::DecimalInteger},
        {"h",       FormatSpecifier::HexadecimalInteger},
        {"dynamic", FormatSpecifier::Dynamic},
        {"nse",     FormatSpecifier::EvaluatesWithNoSideEffects},
        {"nq",      FormatSpecifier::StringWithNoQuotes},
        {"hidden",  FormatSpecifier::DisplaysHiddenMembers},
        {"raw",     FormatSpecifier::DisplaysInRawMode},
        {"results", FormatSpecifier::Results}
    };

    specifier = FormatSpecifier::None;
    expression = expressionWithFormat;

    // Find the last comma to isolate the potential specifier suffix.
    size_t commaPos = expression.rfind(',');

    while (commaPos != std::string::npos)
    {
        // Extract the substring strictly after the comma.
        const std::string_view tail = std::string_view(expression).substr(commaPos + 1);

        const auto find = formatMap.find(tail);
        if (find == formatMap.cend())
        {
            // Stop as soon as a comma-separated tail is not a known specifier:
            // the remaining text is the actual expression, which may legitimately
            // contain commas (e.g. multi-dimensional array access like arr[0,1]).
            break;
        }

        specifier = specifier | find->second;
        expression.resize(commaPos);

        commaPos = expression.rfind(',');
    }
}

} // namespace dncdbg::EvalFormatSpecifiers
