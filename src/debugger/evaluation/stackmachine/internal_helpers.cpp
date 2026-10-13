// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/internal_helpers.h"
#include "debugger/evaluation/evalhelpers/debuginfo.h"
#include "debugger/evaluation/evalhelpers/evalresolver.h"
#include "debugger/evaluation/evalhelpers/evalexec.h"
#include "debugger/evaluation/evalhelpers/value.h"
#include "debugger/evaluation/walkers/walkers.h"
#include "metadata/helpers.h"
#include "utils/hresult.h"
#include <sstream>
#include <utility>

namespace dncdbg::EvalStackMachine
{

namespace
{

void ReplaceAllSubstring(std::string &str, const std::string &from, const std::string &to)
{
    size_t start = 0;
    while (true)
    {
        start = str.find(from, start);
        if (start == std::string::npos)
        {
            break;
        }

        str.replace(start, from.length(), to);
        start += from.length();
    }
}

} // unnamed namespace

HRESULT GetFrontStackEntryValue(std::list<EvalStackEntry> &evalStack, const EvalData &ed, ICorDebugValue **ppResultValue,
                                std::unique_ptr<Walkers::SetterData> *pResultSetterData, std::string &output)
{
    HRESULT Status = S_OK;
    Walkers::SetterData *pInputPropertyData = nullptr;
    if (evalStack.front().editable)
    {
        pInputPropertyData = evalStack.front().setterData.get();
    }
    else
    {
        pResultSetterData = nullptr;
    }

    ICorDebugValue *pForcedThisValue = evalStack.front().trValue == nullptr ? ed.pForcedThisValue : evalStack.front().trValue;
    if (SUCCEEDED(Status = EvalResolver::ResolveIdentifiers(ed.pThread, ed.frameLevel, pForcedThisValue, pInputPropertyData,
                                                            evalStack.front().identifiers, ed.specifier, ppResultValue,
                                                            &evalStack.front().realDisplayTypeName, pResultSetterData, nullptr)))
    {
        return S_OK;
    }

    if (pForcedThisValue == nullptr && !evalStack.front().identifiers.empty())
    {
        PDB::ImportsAndAliases pdbImports;
        EvalDebugInfoHelpers::GetImportsAndAliases(ed.pThread, ed.frameLevel, pdbImports);

        const auto importType = pdbImports.find(PDB::ImportsKind::ImportType);
        if (importType != pdbImports.cend())
        {
            for (const auto &entry : importType->second)
            {
                std::vector<std::string> testIdentifiers = evalStack.front().identifiers;
                const std::vector<std::string> importTypeIdentifiers = MetadataHelpers::SplitFQDisplayTypeName(entry.displayName);
                testIdentifiers.insert(testIdentifiers.begin(), importTypeIdentifiers.cbegin(), importTypeIdentifiers.cend());

                if (SUCCEEDED(EvalResolver::ResolveIdentifiers(ed.pThread, ed.frameLevel, pForcedThisValue, pInputPropertyData,
                                                               testIdentifiers, ed.specifier, ppResultValue,
                                                               &evalStack.front().realDisplayTypeName, pResultSetterData, nullptr)))
                {
                    return S_OK;
                }
            }
        }
    }

    if (!evalStack.front().identifiers.empty())
    {
        std::ostringstream ss;
        for (size_t i = 0; i < evalStack.front().identifiers.size(); i++)
        {
            if (i != 0)
            {
                ss << ".";
            }
            ss << evalStack.front().identifiers.at(i);
        }
        output = "error: The name '" + ss.str() + "' does not exist in the current context";
    }

    return Status;
}

HRESULT GetFrontStackEntryType(std::list<EvalStackEntry> &evalStack, const EvalData &ed,
                               ICorDebugType **ppResultType, std::string &output)
{
    HRESULT Status = S_OK;
    ToRelease<ICorDebugValue> trValue;
    ICorDebugValue *pForcedThisValue = evalStack.front().trValue == nullptr ? ed.pForcedThisValue : evalStack.front().trValue;
    // ResolveIdentifiers may succeed without producing a type (e.g. when only a
    // value or `this` is resolved and no type identifier is left), leaving *ppResultType
    // null. Treat that as a failure too, otherwise callers would dereference a null pointer.
    if (SUCCEEDED(Status = EvalResolver::ResolveIdentifiers(ed.pThread, ed.frameLevel, pForcedThisValue,
                                                            nullptr, evalStack.front().identifiers,
                                                            ed.specifier, &trValue, nullptr, nullptr, ppResultType)) &&
        *ppResultType != nullptr)
    {
        return S_OK;
    }

    if (!evalStack.front().identifiers.empty())
    {
        std::ostringstream ss;
        for (size_t i = 0; i < evalStack.front().identifiers.size(); i++)
        {
            if (i != 0)
            {
                ss << ".";
            }
            ss << evalStack.front().identifiers.at(i);
        }
        if (trValue == nullptr)
        {
            output = "error: The type or namespace name '" + ss.str() + "' couldn't be found";
        }
        else
        {
            output = "error: '" + ss.str() + "' is a variable but is used like a type";
        }
    }

    if (SUCCEEDED(Status))
    {
        Status = E_FAIL;
    }

    return Status;
}

HRESULT GetArgData(ICorDebugValue *pTypeValue, std::string &metadataTypeName, CorElementType &elemType)
{
    HRESULT Status = S_OK;
    IfFailRet(pTypeValue->GetType(&elemType));
    if (elemType == ELEMENT_TYPE_CLASS || elemType == ELEMENT_TYPE_VALUETYPE ||
        elemType == ELEMENT_TYPE_SZARRAY || elemType == ELEMENT_TYPE_ARRAY)
    {
        ToRelease<ICorDebugValue2> trTypeValue2;
        IfFailRet(pTypeValue->QueryInterface(IID_ICorDebugValue2, reinterpret_cast<void **>(&trTypeValue2)));
        ToRelease<ICorDebugType> trType;
        IfFailRet(trTypeValue2->GetExactType(&trType));
        IfFailRet(MetadataHelpers::GetFQMDTypeNameByICorType(trType, metadataTypeName));
    }
    return S_OK;
}

HRESULT GetRealValueWithType(ICorDebugValue *pValue, ICorDebugValue **ppResultValue, CorElementType *pElemType)
{
    HRESULT Status = S_OK;
    // Dereference and unbox value, since we need the real value.
    ToRelease<ICorDebugValue> trRealValue;
    IfFailRet(EvalValueHelpers::DereferenceAndUnboxValue(pValue, &trRealValue));
    CorElementType elemType = ELEMENT_TYPE_MAX;
    IfFailRet(trRealValue->GetType(&elemType));
    // Note, in case of a class (string is a class), we must use a reference instead.
    if (elemType == ELEMENT_TYPE_STRING || elemType == ELEMENT_TYPE_CLASS)
    {
        pValue->AddRef();
        *ppResultValue = pValue;
        if (pElemType != nullptr)
        {
            *pElemType = elemType;
        }
    }
    else
    {
        *ppResultValue = trRealValue.Detach();
        if (pElemType != nullptr)
        {
            *pElemType = elemType;
        }
    }

    return S_OK;
}

HRESULT CallCastOperator(const std::string &opName, ICorDebugValue *pValue, CorElementType elemRetType,
                         const std::string &typeRetName, ICorDebugValue *pTypeValue, ICorDebugValue **pResultValue, const EvalData &ed)
{
    HRESULT Status = S_OK;
    std::string typeName;
    CorElementType elemType = ELEMENT_TYPE_MAX;
    IfFailRet(GetArgData(pTypeValue, typeName, elemType));

    ToRelease<ICorDebugFunction> trFunc;
    IfFailRet(Walkers::WalkMethods(pValue, true,
        [&](bool isStatic, const std::string &methodName, Walkers::ReturnElementType &methodRet,
            std::vector<SigElementType> &methodArgs, uint32_t /*methodGenParamCount*/,
            const Walkers::GetFunctionCallback &getFunction) -> HRESULT
        {
            if (!isStatic || methodArgs.size() != 1 || opName != methodName ||
                elemRetType != methodRet.elemType || typeRetName != methodRet.metadataTypeName ||
                elemType != methodArgs.at(0).elemType || typeName != methodArgs.at(0).metadataTypeName)
            {
                return S_OK; // Return success to continue walking.
            }

            IfFailRet(getFunction(&trFunc));

            return S_CAN_EXIT; // Fast exit from the loop.
        }));

    if (trFunc == nullptr)
    {
        return E_FAIL;
    }

    return EvalExec::CallFunction(ed.pThread, trFunc, nullptr, nullptr, &pTypeValue,
                                  1, ed.specifier, pResultValue);
}

HRESULT CallCastOperator(const std::string &opName, ICorDebugValue *pValue, ICorDebugValue *pTypeRetValue,
                         ICorDebugValue *pTypeValue, ICorDebugValue **pResultValue, const EvalData &ed)
{
    HRESULT Status = S_OK;
    std::string typeRetName;
    CorElementType elemRetType = ELEMENT_TYPE_MAX;
    IfFailRet(GetArgData(pTypeRetValue, typeRetName, elemRetType));

    return CallCastOperator(opName, pValue, elemRetType, typeRetName, pTypeValue, pResultValue, ed);
}

void ReplaceInternalNames(std::string &expression, bool restore)
{
    static const std::vector<std::pair<std::string, std::string>> internalNamesMap{
        {"$exception", "__INTERNAL_DNCDBG_EXCEPTION_VARIABLE"},
        {"$pid", "__INTERNAL_DNCDBG_PID_VARIABLE"},
        {"$tid", "__INTERNAL_DNCDBG_TID_VARIABLE"}
    };

    for (const auto &[internalVariable, tmpReplacement] : internalNamesMap)
    {
        if (restore)
        {
            ReplaceAllSubstring(expression, tmpReplacement, internalVariable);
        }
        else
        {
            ReplaceAllSubstring(expression, internalVariable, tmpReplacement);
        }
    }
}

} // namespace dncdbg::EvalStackMachine
