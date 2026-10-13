// Copyright (c) 2021-2025 Samsung Electronics Co., Ltd.
// Copyright (c) 2026 Mikhail Kurinnoi
// Distributed under the MIT License.
// See the LICENSE file in the project root for more information.

#ifndef DEBUGGER_EVALUATION_STACKMACHINE_RUN_H
#define DEBUGGER_EVALUATION_STACKMACHINE_RUN_H

#include "debugger/evaluation/stackmachine/internal_helpers.h"
#include <list>
#include <string>

namespace dncdbg::EvalStackMachine
{

HRESULT Run(const EvalData &ed, const std::string &expression, std::list<EvalStackEntry> &evalStack, std::string &output);

} // namespace dncdbg::EvalStackMachine

#endif // DEBUGGER_EVALUATION_STACKMACHINE_RUN_H
