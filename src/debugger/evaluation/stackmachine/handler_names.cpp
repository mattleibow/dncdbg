// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/handler_names.h"
#include "metadata/helpers.h"
#include "utils/hresult.h"
#include <cassert>
#include <list>
#include <string>
#include <utility>
#include <vector>

namespace dncdbg::EvalStackMachine
{

HRESULT IdentifierName(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &/*ed*/)
{
    std::string argString = opcode.str;
    ReplaceInternalNames(argString, true);

    evalStack.emplace_front();
    evalStack.front().identifiers.emplace_back(std::move(argString));
    evalStack.front().editable = true;
    return S_OK;
}

HRESULT GenericName(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    HRESULT Status = S_OK;
    const uint32_t argCount = opcode.count;
    std::string argString = opcode.str;
    std::vector<ToRelease<ICorDebugType>> trGenericValues;
    std::string generics = ">";
    trGenericValues.reserve(argCount);
    for (uint32_t i = 0; i < argCount; i++)
    {
        ToRelease<ICorDebugValue> trValue;
        ToRelease<ICorDebugType> trType;
        ToRelease<ICorDebugValue2> trValue2;
        Status = GetFrontStackEntryValue(evalStack, ed, &trValue, nullptr, output);
        if (Status == S_OK)
        {
            IfFailRet(trValue->QueryInterface(IID_ICorDebugValue2, reinterpret_cast<void **>(&trValue2)));
            IfFailRet(trValue2->GetExactType(&trType));
        }
        else
        {
            IfFailRet(GetFrontStackEntryType(evalStack, ed, &trType, output));
        }
        std::string genericType;
        MetadataHelpers::GetFQDisplayTypeName(trType, genericType);
        generics.insert(0, "," + genericType);
        trGenericValues.emplace_back(trType.Detach());
        evalStack.pop_front();
    }
    generics.erase(0, 1);
    argString += "<" + generics;
    evalStack.emplace_front();
    evalStack.front().identifiers.emplace_back(std::move(argString));
    evalStack.front().trGenericTypeCache = std::move(trGenericValues);
    evalStack.front().editable = true;
    return S_OK;
}

HRESULT MemberBindingExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    assert(evalStack.size() > 1);
    assert(evalStack.front().identifiers.size() == 1); // Exactly one unresolved identifier must be here.
    assert(!evalStack.front().trValue); // The front element should hold only an unresolved identifier.

    std::string identifier = std::move(evalStack.front().identifiers.at(0));
    evalStack.pop_front();

    if (evalStack.front().preventBinding)
    {
        return S_OK;
    }

    HRESULT Status = S_OK;
    ToRelease<ICorDebugValue> trValue;
    std::unique_ptr<Walkers::SetterData> setterData;
    IfFailRet(GetFrontStackEntryValue(evalStack, ed, &trValue, &setterData, output));
    evalStack.front().trValue = trValue.Detach();
    evalStack.front().realDisplayTypeName.clear();
    evalStack.front().identifiers.clear();
    evalStack.front().setterData = std::move(setterData);

    ToRelease<ICorDebugReferenceValue> trReferenceValue;
    IfFailRet(evalStack.front().trValue->QueryInterface(IID_ICorDebugReferenceValue, reinterpret_cast<void **>(&trReferenceValue)));
    BOOL isNull = FALSE;
    IfFailRet(trReferenceValue->IsNull(&isNull));

    if (isNull == TRUE)
    {
        evalStack.front().preventBinding = true;
    }
    else
    {
        evalStack.front().identifiers.emplace_back(std::move(identifier));
    }

    return S_OK;
}

HRESULT SimpleMemberAccessExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &/*ed*/)
{
    assert(evalStack.size() > 1);
    assert(!evalStack.front().trValue); // The front element should hold only an unresolved identifier.
    assert(evalStack.front().identifiers.size() == 1); // Exactly one unresolved identifier must be here.

    std::string identifier = std::move(evalStack.front().identifiers.at(0));
    std::vector<ToRelease<ICorDebugType>> trDebugTypes;
    const size_t genericsCount = evalStack.front().trGenericTypeCache.size();
    if (genericsCount > 0)
    {
        trDebugTypes = std::move(evalStack.front().trGenericTypeCache);
    }
    evalStack.pop_front();
    if (!evalStack.front().preventBinding)
    {
        evalStack.front().identifiers.emplace_back(std::move(identifier));
        evalStack.front().trGenericTypeCache.clear(); // We need only the method's generics, so remove all previous ones if any exist.
        if (genericsCount > 0)
        {
            evalStack.front().trGenericTypeCache = std::move(trDebugTypes);
        }
    }
    return S_OK;
}

HRESULT QualifiedName(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    return SimpleMemberAccessExpression(opcode, evalStack, output, ed);
}

HRESULT ThisExpression(const Parser::Opcode &/*opcode*/, std::list<EvalStackEntry> &evalStack, std::string &/*output*/, const EvalData &/*ed*/)
{
    evalStack.emplace_front();
    evalStack.front().identifiers.emplace_back("this");
    evalStack.front().editable = true;
    return S_OK;
}

} // namespace dncdbg::EvalStackMachine
