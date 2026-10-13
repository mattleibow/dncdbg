// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/handler_literals.h"
#include "debugger/evaluation/evalhelpers/evalexec.h"
#include "debugger/evaluation/evalhelpers/systemtypes.h"
#include "debugger/evaluation/primitivetypes/types.h"
#include "expressionparser/helpers.h"
#include "utils/hresult.h"
#include "utils/utf.h"
#include <list>
#include <string>
#include <vector>

namespace dncdbg::EvalStackMachine
{

HRESULT NumericLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    const bool argRealLiteral = (opcode.count == 1);
    const std::string argString = opcode.str;
    CorElementType elemType = ELEMENT_TYPE_MAX;
    std::vector<uint8_t> data;

    HRESULT Status = S_OK;
    IfFailRet(Parser::DetermineNumericTypeAndData(argString, argRealLiteral, elemType, data, output));

    evalStack.emplace_front();
    evalStack.front().literal = true;
    if (elemType == ELEMENT_TYPE_VALUETYPE)
    {
        ToRelease<ICorDebugClass> trClass;
        IfFailRet(SystemTypes::GetClass(SystemTypes::SystemType::Decimal, &trClass));
        return EvalExec::CreateValueType(ed.pThread, trClass, data.data(), &evalStack.front().trValue);
    }
    else
    {
        return PrimitiveTypes::CreateICorValue(ed.pThread, elemType, data.data(), &evalStack.front().trValue);
    }
}

HRESULT StringLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &ed)
{
    std::string argString = opcode.str;
    ReplaceInternalNames(argString, true);
    evalStack.emplace_front();
    evalStack.front().literal = true;
    return EvalExec::CreateString(ed.pThread, argString, &evalStack.front().trValue);
}

HRESULT CharacterLiteralExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    WSTRING wStr = to_utf16(opcode.str);
    if (wStr.empty() || wStr.size() > 1)
    {
        output = "Failed to parse character.";
        return E_INVALIDARG;
    }

    WCHAR value = wStr.at(0);
    evalStack.emplace_front();
    evalStack.front().literal = true;
    return PrimitiveTypes::CreateICorValue(ed.pThread, ELEMENT_TYPE_CHAR, &value, &evalStack.front().trValue);
}

HRESULT PredefinedType(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    HRESULT Status = S_OK;
    CorElementType elemType = ELEMENT_TYPE_MAX;
    IfFailRet(Parser::ParsePredefinedType(opcode.str, elemType, output));

    evalStack.emplace_front();

    if (elemType == ELEMENT_TYPE_VALUETYPE)
    {
        ToRelease<ICorDebugClass> trClass;
        IfFailRet(SystemTypes::GetClass(SystemTypes::SystemType::Decimal, &trClass));
        return EvalExec::CreateValueType(ed.pThread, trClass, nullptr, &evalStack.front().trValue);
    }
    else if (elemType == ELEMENT_TYPE_STRING)
    {
        static const std::string emptyString;
        return EvalExec::CreateString(ed.pThread, emptyString, &evalStack.front().trValue);
    }
    else if (elemType == ELEMENT_TYPE_I)
    {
        ToRelease<ICorDebugClass> trClass;
        IfFailRet(SystemTypes::GetClass(SystemTypes::SystemType::IntPtr, &trClass));
        return EvalExec::CreateValueType(ed.pThread, trClass, nullptr, &evalStack.front().trValue);
    }
    else if (elemType == ELEMENT_TYPE_U)
    {
        ToRelease<ICorDebugClass> trClass;
        IfFailRet(SystemTypes::GetClass(SystemTypes::SystemType::UIntPtr, &trClass));
        return EvalExec::CreateValueType(ed.pThread, trClass, nullptr, &evalStack.front().trValue);
    }
    else
    {
        return PrimitiveTypes::CreateICorValue(ed.pThread, elemType, nullptr, &evalStack.front().trValue);
    }
}

HRESULT TrueLiteralExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &ed)
{
    evalStack.emplace_front();
    evalStack.front().literal = true;
    return EvalExec::CreateBooleanValue(ed.pThread, &evalStack.front().trValue, true);
}

HRESULT FalseLiteralExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &ed)
{
    evalStack.emplace_front();
    evalStack.front().literal = true;
    return EvalExec::CreateBooleanValue(ed.pThread, &evalStack.front().trValue, false);
}

HRESULT NullLiteralExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &ed)
{
    evalStack.emplace_front();
    evalStack.front().literal = true;
    return EvalExec::CreateNullValue(ed.pThread, &evalStack.front().trValue);
}

} // namespace dncdbg::EvalStackMachine
