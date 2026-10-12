// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_EVALHELPERS_FORMATSPECIFIER_H
#define DEBUGGER_EVALUATION_EVALHELPERS_FORMATSPECIFIER_H

#include "types/types.h"
#include <string>

namespace dncdbg::EvalFormatSpecifiers
{

// Parse the format specifier suffix (e.g. "expression,raw,h") from an expression to evaluate:
// "expression" receives the expression without the format specifier suffix,
// "specifier" receives the flags of all recognized format specifiers.
void ParseFormatSpecifier(const std::string &expressionWithFormat, std::string &expression, FormatSpecifier &specifier);

} // namespace dncdbg::EvalFormatSpecifiers

#endif // DEBUGGER_EVALUATION_EVALHELPERS_FORMATSPECIFIER_H
