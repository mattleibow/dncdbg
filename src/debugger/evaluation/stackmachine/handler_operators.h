// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_OPERATORS_H
#define DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_OPERATORS_H

#include "debugger/evaluation/stackmachine/internal_helpers.h"
#include "expressionparser/parser.h"
#include <list>
#include <string>

namespace dncdbg::EvalStackMachine
{

HRESULT BinaryOperator(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT UnaryOperator(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT CoalesceExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT SizeOfExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

} // namespace dncdbg::EvalStackMachine

#endif // DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_OPERATORS_H
