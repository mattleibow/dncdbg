// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_STACKMACHINE_INTERNAL_HELPERS_H
#define DEBUGGER_EVALUATION_STACKMACHINE_INTERNAL_HELPERS_H

#include <cor.h>
#include <cordebug.h>
#ifdef FEATURE_PAL
#include <specstrings_undef.h>
#endif

#include "debugger/evaluation/walkers/types.h"
#include "types/types.h"
#include "utils/torelease.h"
#include <list>
#include <memory>
#include <string>
#include <vector>

namespace dncdbg::EvalStackMachine
{

struct EvalStackEntry
{
    enum class ResetLiteralStatus : uint8_t
    {
        No = 0,
        Yes = 1
    };

    // Unresolved identifiers.
    // Note, if some identifiers are already resolved (trValue), the remaining ones must be resolved within trValue.
    std::vector<std::string> identifiers;
    // Identifiers resolved to a value.
    ToRelease<ICorDebugValue> trValue;
    // Real display type name of the resolved value, more precise than what GetFQDisplayTypeName() may provide
    // (e.g. for literal array types). Empty when the type should be obtained via regular metadata lookup.
    std::string realDisplayTypeName;
    // Generic types cache. Note, in the end we only need the method's generic types, i.e. the last element of
    // the identifiers vector. The generics of the other (class) types can easily be obtained from the corresponding ICorDebugType.
    std::vector<ToRelease<ICorDebugType>> trGenericTypeCache;
    // Prevent further binding in case of conditional access on a null object (`a?.b`, `a?[1]`, ...).
    // Note, this state relates to trValue only (trValue must be checked for null first).
    bool preventBinding{false};
    // This is a literal entry (the value was created from a literal).
    bool literal{false};
    // This entry is a real variable (not a literal, not the result of an expression, not the result of a function call, ...).
    bool editable{false};
    // If trValue is an editable property, we need extra data in order to set the value.
    // Note, this data is directly connected to `trValue` and is available only when `editable` is true.
    std::unique_ptr<Walkers::SetterData> setterData;

    void ResetEntry(ResetLiteralStatus resetLiteral = ResetLiteralStatus::Yes)
    {
        identifiers.clear();
        trValue.Free();
        realDisplayTypeName.clear();
        trGenericTypeCache.clear();
        preventBinding = false;
        if (resetLiteral == ResetLiteralStatus::Yes)
        {
            literal = false;
        }
        editable = false;
        setterData.reset();
    }
};

struct EvalData
{
    ICorDebugThread *pThread{nullptr};
    FrameLevel frameLevel;
    FormatSpecifier specifier{FormatSpecifier::None};
    ICorDebugValue *pForcedThisValue{nullptr};
};

HRESULT GetFrontStackEntryValue(std::list<EvalStackEntry> &evalStack, const EvalData &ed, ICorDebugValue **ppResultValue,
                                std::unique_ptr<Walkers::SetterData> *pResultSetterData, std::string &output);

HRESULT GetFrontStackEntryType(std::list<EvalStackEntry> &evalStack, const EvalData &ed,
                               ICorDebugType **ppResultType, std::string &output);

HRESULT GetArgData(ICorDebugValue *pTypeValue, std::string &metadataTypeName, CorElementType &elemType);

HRESULT CallCastOperator(const std::string &opName, ICorDebugValue *pValue, CorElementType elemRetType,
                         const std::string &typeRetName, ICorDebugValue *pTypeValue, ICorDebugValue **pResultValue, const EvalData &ed);

HRESULT CallCastOperator(const std::string &opName, ICorDebugValue *pValue, ICorDebugValue *pTypeRetValue,
                         ICorDebugValue *pTypeValue, ICorDebugValue **pResultValue, const EvalData &ed);

HRESULT GetRealValueWithType(ICorDebugValue *pValue, ICorDebugValue **ppResultValue, CorElementType *pElemType = nullptr);

void ReplaceInternalNames(std::string &expression, bool restore = false);

} // namespace dncdbg::EvalStackMachine

#endif // DEBUGGER_EVALUATION_STACKMACHINE_INTERNAL_HELPERS_H
