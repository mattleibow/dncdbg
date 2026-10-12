// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/evalhelpers/textwitheval.h"
#include "debugger/evaluation/evalhelpers/formatspecifier.h"
#include "debugger/evalstackmachine.h"
#include "debugger/valueprint.h"
#include "utils/torelease.h"

namespace dncdbg::EvalTextHelpers
{

void CreateTextWithEvalParts(const std::string &textWithEval, std::vector<std::pair<std::string, bool>> &textWithEvalParts)
{
    size_t pos = 0;
    size_t prevPos = 0;

    while ((pos = textWithEval.find('{', prevPos)) != std::string::npos)
    {
        // Add text before the '{' (if any) as literal text.
        if (pos > prevPos)
        {
            textWithEvalParts.emplace_back(textWithEval.substr(prevPos, pos - prevPos), false);
        }

        // Find the matching closing '}' by counting brace depth.
        size_t endPos = pos + 1;
        int braceDepth = 1;
        while (endPos < textWithEval.length() && braceDepth > 0)
        {
            if (textWithEval.at(endPos) == '{')
            {
                braceDepth++;
            }
            else if (textWithEval.at(endPos) == '}')
            {
                braceDepth--;
            }
            endPos++;
        }

        if (braceDepth > 0)
        {
            // No matching closing brace found; treat everything from '{' to the end as literal text.
            textWithEvalParts.emplace_back(textWithEval.substr(pos), false);
            prevPos = textWithEval.length();
            break;
        }

        // Add the expression inside braces (without the braces themselves) as an expression.
        // endPos points to the position right after the matching '}', so the expression is [pos+1, endPos-1).
        textWithEvalParts.emplace_back(textWithEval.substr(pos + 1, endPos - pos - 2), true);

        prevPos = endPos;
    }

    // Add the remaining text after the last '}' (or the entire string if no braces were found) as literal text.
    if (prevPos < textWithEval.length())
    {
        textWithEvalParts.emplace_back(textWithEval.substr(prevPos), false);
    }
}

void BuildTextWithEval(ICorDebugThread *pThread, ICorDebugValue *pForcedThisValue,
                       const std::vector<std::pair<std::string, bool>> &textWithEvalParts, std::string &output)
{
    // Build the final output text by evaluating expressions.
    for (const auto &[text, isExpression] : textWithEvalParts)
    {
        if (!isExpression)
        {
            // Literal text - append directly.
            output += text;
        }
        else
        {
            // Expression - evaluate it.
            FormatSpecifier specifier = FormatSpecifier::None;
            std::string expression;
            EvalFormatSpecifiers::ParseFormatSpecifier(text, expression, specifier);

            std::string value;
            std::string errorText;
            ToRelease<ICorDebugValue> trResultValue;
            if (SUCCEEDED(EvalStackMachine::EvaluateExpression(pThread, FrameLevel{0}, expression,
                                                               pForcedThisValue == nullptr ? specifier : specifier | FormatSpecifier::DisplaysInRawMode,
                                                               pForcedThisValue, &trResultValue, nullptr, errorText)) &&
                SUCCEEDED(PrintValue(pThread, trResultValue, specifier, value)))
            {
                output += value;
            }
            else
            {
                if (!errorText.empty())
                {
                    output += "{" + errorText + "}";
                }
                else
                {
                    output += "{unknown error}";
                }
            }
        }
    }
}

} // namespace dncdbg::EvalTextHelpers
