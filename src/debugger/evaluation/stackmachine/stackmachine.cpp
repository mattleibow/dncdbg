// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#include "debugger/evaluation/stackmachine/stackmachine.h"
#include "debugger/evaluation/primitivetypes/types.h"
#include "debugger/evaluation/stackmachine/internal_helpers.h"
#include "debugger/evaluation/stackmachine/run.h"
#include "metadata/helpers.h"
#include "utils/hresult.h"
#include "utils/torelease.h"
#include <cassert>
#include <list>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace dncdbg::EvalStackMachine
{

namespace
{

HRESULT CopyValue(ICorDebugValue *pSrcValue, ICorDebugValue *pDstValue, CorElementType elemTypeSrc, CorElementType elemTypeDst)
{
    if (elemTypeSrc != elemTypeDst)
    {
        return E_INVALIDARG;
    }

    HRESULT Status = S_OK;
    // Change address.
    if (elemTypeDst == ELEMENT_TYPE_STRING || elemTypeDst == ELEMENT_TYPE_CLASS)
    {
        ToRelease<ICorDebugReferenceValue> trRefNew;
        IfFailRet(pSrcValue->QueryInterface(IID_ICorDebugReferenceValue, reinterpret_cast<void **>(&trRefNew)));
        ToRelease<ICorDebugReferenceValue> trRefOld;
        IfFailRet(pDstValue->QueryInterface(IID_ICorDebugReferenceValue, reinterpret_cast<void **>(&trRefOld)));

        CORDB_ADDRESS addr = 0;
        IfFailRet(trRefNew->GetValue(&addr));
        return trRefOld->SetValue(addr);
    }

    // Copy data.
    if (PrimitiveTypes::IsPrimitiveType(elemTypeDst) ||
        elemTypeDst == ELEMENT_TYPE_VALUETYPE)
    {
        uint32_t cbSize = 0;
        IfFailRet(pSrcValue->GetSize(&cbSize));
        std::vector<uint8_t> elemValue(cbSize, 0);

        ToRelease<ICorDebugGenericValue> trGenericValue;
        IfFailRet(pSrcValue->QueryInterface(IID_ICorDebugGenericValue, reinterpret_cast<void **>(&trGenericValue)));
        IfFailRet(trGenericValue->GetValue(static_cast<void *>(elemValue.data())));

        trGenericValue.Free();
        IfFailRet(pDstValue->QueryInterface(IID_ICorDebugGenericValue, reinterpret_cast<void **>(&trGenericValue)));
        return trGenericValue->SetValue(elemValue.data());
    }

    return E_NOTIMPL;
}

HRESULT ImplicitCast(ICorDebugValue *pSrcValue, ICorDebugValue *pDstValue, bool srcLiteral, const EvalData &ed)
{
    HRESULT Status = S_OK;

    // A value with a type was provided by the caller, and the result must be implicitly cast to this type.
    // https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/conversions#implicit-numeric-conversions
    // https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types#integer-literals

    ToRelease<ICorDebugValue> trRealValue1;
    CorElementType elemType1 = ELEMENT_TYPE_MAX;
    IfFailRet(GetRealValueWithType(pSrcValue, &trRealValue1, &elemType1));

    ToRelease<ICorDebugValue> trRealValue2;
    CorElementType elemType2 = ELEMENT_TYPE_MAX;
    IfFailRet(GetRealValueWithType(pDstValue, &trRealValue2, &elemType2));

    bool haveSameType = true;
    if (elemType1 == elemType2)
    {
        if (elemType2 == ELEMENT_TYPE_VALUETYPE || elemType2 == ELEMENT_TYPE_CLASS)
        {
            std::string metadataName1;
            IfFailRet(MetadataHelpers::GetFQMDTypeNameByICorValue(trRealValue1, metadataName1));
            std::string metadataName2;
            IfFailRet(MetadataHelpers::GetFQMDTypeNameByICorValue(trRealValue2, metadataName2));

            if (metadataName1 != metadataName2)
            {
                haveSameType = false;
            }
        }
    }
    else
    {
        haveSameType = false;
    }

    if (!haveSameType && (elemType1 == ELEMENT_TYPE_VALUETYPE || elemType2 == ELEMENT_TYPE_VALUETYPE ||
                          elemType1 == ELEMENT_TYPE_CLASS || elemType2 == ELEMENT_TYPE_CLASS ||
                          elemType1 == ELEMENT_TYPE_STRING || elemType2 == ELEMENT_TYPE_STRING))
    {
        ToRelease<ICorDebugValue> trResultValue;
        if (FAILED(Status = CallCastOperator("op_Implicit", trRealValue1, trRealValue2, trRealValue1, &trResultValue, ed)) &&
            FAILED(Status = CallCastOperator("op_Implicit", trRealValue2, trRealValue2, trRealValue1, &trResultValue, ed)))
        {
            return Status;
        }

        trRealValue1.Free();
        IfFailRet(GetRealValueWithType(trResultValue, &trRealValue1, &elemType1));

        haveSameType = true;
    }

    if (haveSameType)
    {
        return CopyValue(trRealValue1, trRealValue2, elemType1, elemType2);
    }

    if (!PrimitiveTypes::IsPrimitiveType(elemType1) || !PrimitiveTypes::IsPrimitiveType(elemType2))
    {
        return E_INVALIDARG;
    }

    // Both int (ELEMENT_TYPE_I4) and long (ELEMENT_TYPE_I8) constant expressions follow
    // the implicit constant expression conversion rules (ECMA-334).
    return (srcLiteral && (elemType1 == ELEMENT_TYPE_I4 || elemType1 == ELEMENT_TYPE_I8)) ?
        PrimitiveTypes::ImplicitCastLiteral(trRealValue1, trRealValue2) :
        PrimitiveTypes::ImplicitCast(trRealValue1, trRealValue2);
}

} // unnamed namespace

HRESULT EvaluateExpression(ICorDebugThread *pThread, FrameLevel frameLevel, const std::string &expression,
                           FormatSpecifier specifier, ICorDebugValue *pForcedThisValue,
                           ICorDebugValue **ppResultValue, std::string *pRealDisplayTypeName, std::string &output,
                           bool *pEditable, std::unique_ptr<Walkers::SetterData> *pResultSetterData)
{
    HRESULT Status = S_OK;
    std::list<EvalStackEntry> evalStack;
    EvalData ed;
    ed.pThread = pThread;
    ed.frameLevel = frameLevel;
    ed.specifier = specifier | FormatSpecifier::WalkContainerMembers;
    ed.pForcedThisValue = pForcedThisValue;
    IfFailRet(Run(ed, expression, evalStack, output));

    assert(evalStack.size() == 1);

    std::unique_ptr<Walkers::SetterData> setterData;
    IfFailRet(GetFrontStackEntryValue(evalStack, ed, ppResultValue, &setterData, output));
    if (pRealDisplayTypeName != nullptr)
    {
        *pRealDisplayTypeName = evalStack.front().realDisplayTypeName;
    }

    if (pEditable != nullptr)
    {
        *pEditable = (setterData != nullptr) && (setterData->trSetterFunction == nullptr) ? false /*property doesn't have a setter*/
                                                                                          : evalStack.front().editable;
    }

    if (pResultSetterData != nullptr)
    {
        *pResultSetterData = std::move(setterData);
    }

    return S_OK;
}

HRESULT SetValueByExpression(ICorDebugThread *pThread, FrameLevel frameLevel, ICorDebugValue *pValue,
                             const std::string &expression, std::string &output)
{
    HRESULT Status = S_OK;
    std::list<EvalStackEntry> evalStack;
    EvalData ed;
    ed.pThread = pThread;
    ed.frameLevel = frameLevel;
    ed.specifier = FormatSpecifier::None;
    ed.pForcedThisValue = nullptr;
    IfFailRet(Run(ed, expression, evalStack, output));

    assert(evalStack.size() == 1);

    ToRelease<ICorDebugValue> trValue;
    IfFailRet(GetFrontStackEntryValue(evalStack, ed, &trValue, nullptr, output));

    return ImplicitCast(trValue, pValue, evalStack.front().literal, ed);
}

} // namespace dncdbg::EvalStackMachine
