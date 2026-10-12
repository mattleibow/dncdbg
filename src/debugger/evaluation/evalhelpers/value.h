// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_EVALHELPERS_VALUE_H
#define DEBUGGER_EVALUATION_EVALHELPERS_VALUE_H

#include <cor.h>
#include <cordebug.h>
#ifdef FEATURE_PAL
#include <specstrings_undef.h>
#endif

#include "types/types.h"
#include <vector>

namespace dncdbg::EvalValueHelpers
{

// From strike.cpp
HRESULT DereferenceAndUnboxValue(ICorDebugValue *pValue, ICorDebugValue **ppOutputValue, BOOL *pIsNull = nullptr);

HRESULT GetNullableValue(ICorDebugValue *pValue, ICorDebugValue **ppValueValue, ICorDebugValue **ppHasValueValue);
HRESULT GetNullableValue(ICorDebugValue *pValue, ICorDebugValue **ppValueValue, bool &hasValue);

HRESULT GetDictionaryItemValue(ICorDebugThread *pThread, ICorDebugValue *pValue,
                               FormatSpecifier specifier, ICorDebugValue **ppItemValue);

HRESULT GetArrayElement(ICorDebugValue *pInputValue, std::vector<uint32_t> &indexes, ICorDebugValue **ppResultValue);

// Check whether the base type is System.Enum.
bool IsEnumeration(ICorDebugType *pInputType);
bool IsEnumeration(ICorDebugValue *pInputValue);

} // namespace dncdbg::EvalValueHelpers

#endif // DEBUGGER_EVALUATION_EVALHELPERS_VALUE_H
