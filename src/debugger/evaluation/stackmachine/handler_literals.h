// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_LITERALS_H
#define DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_LITERALS_H

#include "debugger/evaluation/stackmachine/internal_helpers.h"
#include "expressionparser/parser.h"
#include <list>
#include <string>

namespace dncdbg::EvalStackMachine
{

HRESULT NumericLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT StringLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT CharacterLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT PredefinedType(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT TrueLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT FalseLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

HRESULT NullLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed);

} // namespace dncdbg::EvalStackMachine

#endif // DEBUGGER_EVALUATION_STACKMACHINE_HANDLER_LITERALS_H
