// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/run.h"
#include "debugger/evaluation/stackmachine/handler_elementaccess.h"
#include "debugger/evaluation/stackmachine/handler_invocation.h"
#include "debugger/evaluation/stackmachine/handler_literals.h"
#include "debugger/evaluation/stackmachine/handler_names.h"
#include "debugger/evaluation/stackmachine/handler_operators.h"
#include "expressionparser/parser.h"
#include "utils/hresult.h"
#include "utils/logger.h"
#include <functional>
#include <iomanip>
#include <list>
#include <string>
#include <unordered_map>

namespace dncdbg::EvalStackMachine
{

HRESULT Run(const EvalData &ed, const std::string &expression, std::list<EvalStackEntry> &evalStack, std::string &output)
{
    static const std::unordered_map<Parser::SyntaxKind, std::function<HRESULT(const Parser::Opcode &, std::list<EvalStackEntry> &, std::string &, const EvalData &)>> CommandImplementation{
        {Parser::SyntaxKind::IdentifierName, IdentifierName},
        {Parser::SyntaxKind::GenericName, GenericName},
        {Parser::SyntaxKind::InvocationExpression, InvocationExpression},
        {Parser::SyntaxKind::ObjectCreationExpression, ObjectCreationExpression},
        {Parser::SyntaxKind::ElementAccessExpression, ElementAccessExpression},
        {Parser::SyntaxKind::ElementBindingExpression, ElementBindingExpression},
        {Parser::SyntaxKind::NumericLiteralExpression, NumericLiteralExpression},
        {Parser::SyntaxKind::StringLiteralExpression, StringLiteralExpression},
        {Parser::SyntaxKind::CharacterLiteralExpression, CharacterLiteralExpression},
        {Parser::SyntaxKind::PredefinedType, PredefinedType},
        {Parser::SyntaxKind::QualifiedName, QualifiedName},
        {Parser::SyntaxKind::MemberBindingExpression, MemberBindingExpression},
        {Parser::SyntaxKind::SimpleMemberAccessExpression, SimpleMemberAccessExpression},
        {Parser::SyntaxKind::AddExpression, BinaryOperator},
        {Parser::SyntaxKind::MultiplyExpression, BinaryOperator},
        {Parser::SyntaxKind::SubtractExpression, BinaryOperator},
        {Parser::SyntaxKind::DivideExpression, BinaryOperator},
        {Parser::SyntaxKind::ModuloExpression, BinaryOperator},
        {Parser::SyntaxKind::LeftShiftExpression, BinaryOperator},
        {Parser::SyntaxKind::RightShiftExpression, BinaryOperator},
        {Parser::SyntaxKind::UnsignedRightShiftExpression, BinaryOperator},
        {Parser::SyntaxKind::BitwiseAndExpression, BinaryOperator},
        {Parser::SyntaxKind::BitwiseOrExpression, BinaryOperator},
        {Parser::SyntaxKind::ExclusiveOrExpression, BinaryOperator},
        {Parser::SyntaxKind::LogicalAndExpression, BinaryOperator},
        {Parser::SyntaxKind::LogicalOrExpression, BinaryOperator},
        {Parser::SyntaxKind::EqualsExpression, BinaryOperator},
        {Parser::SyntaxKind::NotEqualsExpression, BinaryOperator},
        {Parser::SyntaxKind::GreaterThanExpression, BinaryOperator},
        {Parser::SyntaxKind::LessThanExpression, BinaryOperator},
        {Parser::SyntaxKind::GreaterThanOrEqualExpression, BinaryOperator},
        {Parser::SyntaxKind::LessThanOrEqualExpression, BinaryOperator},
        {Parser::SyntaxKind::UnaryPlusExpression, UnaryOperator},
        {Parser::SyntaxKind::UnaryMinusExpression, UnaryOperator},
        {Parser::SyntaxKind::LogicalNotExpression, UnaryOperator},
        {Parser::SyntaxKind::BitwiseNotExpression, UnaryOperator},
        {Parser::SyntaxKind::TrueLiteralExpression, TrueLiteralExpression},
        {Parser::SyntaxKind::FalseLiteralExpression, FalseLiteralExpression},
        {Parser::SyntaxKind::NullLiteralExpression, NullLiteralExpression},
        {Parser::SyntaxKind::SizeOfExpression, SizeOfExpression},
        {Parser::SyntaxKind::CoalesceExpression, CoalesceExpression},
        {Parser::SyntaxKind::ThisExpression, ThisExpression}
    };

    // Note, internal variables start with "$" and must be replaced before the C# syntax analysis.
    // The original names will be restored after the C# syntax analysis in IdentifierName and StringLiteralExpression.
    std::string fixedExpression = expression;
    ReplaceInternalNames(fixedExpression);

    HRESULT Status = S_OK;
    std::list<Parser::Opcode> stackProgram;
    IfFailRet(Parser::GenerateProgram(fixedExpression, stackProgram, output));

    for (const auto &executionStep : stackProgram)
    {
        const auto findStep = CommandImplementation.find(executionStep.kind);
        if (findStep == CommandImplementation.cend())
        {
            output = "Invalid syntax kind.";
            Status = E_INVALIDARG;
            break;
        }

        if (FAILED(Status = findStep->second(executionStep, evalStack, output, ed)))
        {
            break;
        }
    }

    switch (Status)
    {
    case CORDBG_E_ILLEGAL_IN_PROLOG:
    case CORDBG_E_ILLEGAL_IN_NATIVE_CODE:
    case CORDBG_E_ILLEGAL_IN_STACK_OVERFLOW:
    case CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE:
    case CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT:
        output = "This expression causes side effects and will not be evaluated.";
        LOGE(log << "Eval error: 0x" << std::setw(hexErrWidth) << std::setfill('0') << std::hex << Status);
        break;
    case COR_E_TIMEOUT:
        output = "Evaluation timed out.";
        break;
    case COR_E_OPERATIONCANCELED:
        output = "Evaluation canceled by user.";
        break;
    case CORDBG_E_CANT_CALL_ON_THIS_THREAD:
        output = "The function evaluation requires all threads to run.";
        break;
    case E_UNEXPECTED:
        output = "Evaluation timed out, but function evaluation can't be completed or aborted. Debuggee has inconsistent state now.";
        break;
    case COR_E_THREADSTATE:
        output = "Thread is in an invalid state for this operation.";
        break;
    case CORDBG_E_DEBUGGING_DISABLED:
        output = "Implicit function evaluation is turned off by the user.";
        break;
    default:
        break;
    }

    return Status;
}

} // namespace dncdbg::EvalStackMachine
