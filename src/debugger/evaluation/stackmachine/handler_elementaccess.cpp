// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/handler_elementaccess.h"
#include "debugger/evaluation/evalhelpers/evalexec.h"
#include "debugger/evaluation/evalhelpers/value.h"
#include "debugger/evaluation/primitivetypes/types.h"
#include "debugger/evaluation/walkers/walkers.h"
#include "utils/hresult.h"
#include <list>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace dncdbg::EvalStackMachine
{

namespace
{

HRESULT ElementAccessHelper(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output,
                            const EvalData &ed, bool preventBindingOnNull)
{
    const uint32_t argCount = opcode.count;
    HRESULT Status = S_OK;

    std::vector<ToRelease<ICorDebugValue>> trIndexValues(argCount);

    uint32_t tmpArgCount = opcode.count;
    while (tmpArgCount > 0)
    {
        tmpArgCount--;
        IfFailRet(GetFrontStackEntryValue(evalStack, ed, &trIndexValues.at(tmpArgCount), nullptr, output));
        evalStack.pop_front();
    }
    if (evalStack.front().preventBinding)
    {
        return S_OK;
    }

    ToRelease<ICorDebugValue> trObjectValue;
    std::unique_ptr<Walkers::SetterData> setterData;
    IfFailRet(GetFrontStackEntryValue(evalStack, ed, &trObjectValue, &setterData, output));

    ToRelease<ICorDebugValue> trRealValue;
    BOOL isNull = FALSE;
    IfFailRet(EvalValueHelpers::DereferenceAndUnboxValue(trObjectValue, &trRealValue, &isNull));
    if (isNull == TRUE)
    {
        if (preventBindingOnNull)
        {
            evalStack.front().preventBinding = true;
            return S_OK;
        }

        return E_INVALIDARG;
    }

    CorElementType elemType = ELEMENT_TYPE_MAX;
    IfFailRet(trRealValue->GetType(&elemType));

    if (elemType == ELEMENT_TYPE_SZARRAY || elemType == ELEMENT_TYPE_ARRAY)
    {
        std::vector<uint32_t> indexes;
        uint32_t tmpArgCount = opcode.count;
        while (tmpArgCount > 0)
        {
            tmpArgCount--;
            uint32_t index = 0;
            // ICorDebugArrayValue::GetElement expects uint32_t indices.
            IfFailRet(PrimitiveTypes::ForceCastToUint(trIndexValues.at(tmpArgCount), index));
            indexes.insert(indexes.begin(), index);
        }
        evalStack.front().trValue.Free();
        evalStack.front().realDisplayTypeName.clear();
        evalStack.front().identifiers.clear();
        evalStack.front().setterData = std::move(setterData);
        Status = EvalValueHelpers::GetArrayElement(trRealValue, indexes, &evalStack.front().trValue);
    }
    else if (elemType == ELEMENT_TYPE_STRING ||
             elemType == ELEMENT_TYPE_CLASS ||
             elemType == ELEMENT_TYPE_VALUETYPE)
    {
        std::vector<SigElementType> funcArgs(argCount);
        for (uint32_t i = 0; i < argCount; ++i)
        {
            ToRelease<ICorDebugValue> trValueArg;
            IfFailRet(EvalValueHelpers::DereferenceAndUnboxValue(trIndexValues.at(i), &trValueArg, nullptr));
            IfFailRet(GetArgData(trValueArg, funcArgs.at(i).metadataTypeName, funcArgs.at(i).elemType));
        }

        ToRelease<ICorDebugValue2> trRealValue2;
        IfFailRet(trRealValue->QueryInterface(IID_ICorDebugValue2, reinterpret_cast<void **>(&trRealValue2)));
        ToRelease<ICorDebugType> trRealType;
        IfFailRet(trRealValue2->GetExactType(&trRealType));

        ToRelease<ICorDebugFunction> trFunc;
        IfFailRet(Walkers::WalkIndexers(trRealType,
            [&](std::vector<SigElementType> &methodArgs,
                const Walkers::GetFunctionCallback &getFunction) -> HRESULT
            {
                if (funcArgs.size() != methodArgs.size())
                {
                    return S_OK; // Return success to continue walking.
                }

                for (size_t i = 0; i < funcArgs.size(); ++i)
                {
                    // TODO: implicit casts must be handled here.
                    if (funcArgs.at(i) != methodArgs.at(i))
                    {
                        return S_OK; // Return success to continue walking.
                    }
                }
                IfFailRet(getFunction(&trFunc));
                return S_CAN_EXIT; // Fast exit from the loop, since we already found trFunc.
            }));

        if (trFunc == nullptr)
        {
            return E_INVALIDARG;
        }

        evalStack.front().ResetEntry();
        std::vector<ICorDebugValue *> pValueArgs;
        pValueArgs.reserve(argCount + 1);

        pValueArgs.emplace_back(trObjectValue.GetPtr());

        for (uint32_t i = 0; i < argCount; i++)
        {
            pValueArgs.emplace_back(trIndexValues.at(i).GetPtr());
        }

        ToRelease<ICorDebugValue2> trValue2;
        IfFailRet(trObjectValue->QueryInterface(IID_ICorDebugValue2, reinterpret_cast<void **>(&trValue2)));
        ToRelease<ICorDebugType> trType;
        IfFailRet(trValue2->GetExactType(&trType));

        Status = EvalExec::CallFunction(ed.pThread, trFunc, trType.GetPtr(), nullptr, pValueArgs.data(),
                                        argCount + 1, ed.specifier, &evalStack.front().trValue);
    }
    else
    {
        return E_INVALIDARG;
    }

    return Status;
}

} // unnamed namespace

HRESULT ElementAccessExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    return ElementAccessHelper(opcode, evalStack, output, ed, false);
}

HRESULT ElementBindingExpression(const Parser::Opcode &opcode, std::list<EvalStackEntry> &evalStack, std::string &output, const EvalData &ed)
{
    return ElementAccessHelper(opcode, evalStack, output, ed, true);
}

} // namespace dncdbg::EvalStackMachine
