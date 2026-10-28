// Typed preview model on the existing native execution owner; never arbitrary COM dispatch.
#pragma once
#include <algorithm>
#include <cctype>
#include <map>
#include <vector>

constexpr Guid ReferenceValueId{0xcc7bcaf9, 0x8a68, 0x11d2, {0x98, 0x3c, 0, 0, 0xf8, 8, 0x34, 0x2d}};
constexpr Guid StringValueId{0xcc7bcafd, 0x8a68, 0x11d2, {0x98, 0x3c, 0, 0, 0xf8, 8, 0x34, 0x2d}};
constexpr Guid ArrayValueId{0x0405b0df, 0xa660, 0x11d2, {0xbd, 2, 0, 0, 0xf8, 8, 0x49, 0xbd}};
constexpr Guid ObjectValueId{0x18ad3d6e, 0xb7d2, 0x11d2, {0xbd, 4, 0, 0, 0xf8, 8, 0x49, 0xbd}};
struct ModelLease { Unknown* value; bool scope; Json variables; };
struct ModelBinding { Json location; std::string source; int line; Breakpoint* breakpoint = nullptr; };
std::unordered_map<int64_t, ModelLease> modelLeases;
std::map<uint32_t, Thread*> modelThreads;
std::unordered_map<Unknown*, Json> modelModules;
std::vector<ModelBinding> modelBindings;
Stepper* modelStepper = nullptr;

std::string Utf8(const char16_t* text, size_t count) {
    std::string result;
    for (size_t i = 0; i < count; ++i) {
        uint32_t point = text[i];
        if (point >= 0xd800 && point <= 0xdbff && i + 1 < count && text[i + 1] >= 0xdc00 && text[i + 1] <= 0xdfff)
            point = 0x10000 + ((point - 0xd800) << 10) + (text[++i] - 0xdc00);
        else if (point >= 0xd800 && point <= 0xdfff) point = 0xfffd;
        if (point < 0x80) result += static_cast<char>(point);
        else if (point < 0x800) {
            result += static_cast<char>(0xc0 | (point >> 6)); result += static_cast<char>(0x80 | (point & 63));
        } else if (point < 0x10000) {
            result += static_cast<char>(0xe0 | (point >> 12)); result += static_cast<char>(0x80 | ((point >> 6) & 63));
            result += static_cast<char>(0x80 | (point & 63));
        } else {
            result += static_cast<char>(0xf0 | (point >> 18)); result += static_cast<char>(0x80 | ((point >> 12) & 63));
            result += static_cast<char>(0x80 | ((point >> 6) & 63)); result += static_cast<char>(0x80 | (point & 63));
        }
    }
    return result;
}
void Event(Json event) {
    if (mobileEvents.size() >= 4096) throw std::runtime_error("EventLimit");
    mobileEvents.push_back(std::move(event));
}
std::string ModuleMvid(Module* module) {
    Ref metadata;
    if (module->GetMetaDataInterface(MetadataId, &metadata.value) < 0 || !metadata.value) return "";
    char16_t name[1024]{};
    uint32_t length = 0;
    Guid mvid{};
    Require(static_cast<MetadataImport*>(metadata.value)->GetScopeProps(name, 1024, &length, &mvid));
    return Hex(&mvid, sizeof(mvid));
}
Json Symbols(Module* module) {
    std::string mvid = ModuleMvid(module);
    for (auto& entry : catalog) if (entry.at("Mvid") == mvid) return entry;
    return nullptr;
}
Json MethodSymbols(Frame* frame) {
    Ref<Function> function;
    Ref<Module> module;
    uint32_t token = 0;
    Require(frame->GetFunction(&function.value));
    Require(function.value->GetModule(&module.value));
    Require(function.value->GetToken(&token));
    Json symbols = Symbols(module.value);
    if (!symbols.is_null())
        for (auto& method : symbols.at("Methods")) if (method.at("Token") == token) return method;
    return nullptr;
}
Json VisibleVariables(ILFrame* frame) {
    Json method = MethodSymbols(frame), variables = Json::array();
    uint32_t offset = 0, mapping = 0;
    Require(frame->GetIP(&offset, &mapping));
    if (method.is_null()) throw std::runtime_error("SymbolsUnavailable");
    for (auto& item : method.at("Variables"))
        if (offset >= item.value("StartOffset", 0u) && offset < item.value("EndOffset", static_cast<uint32_t>(INT32_MAX)))
            variables.push_back(item);
    return variables;
}
void ModelClear() {
    for (auto& lease : modelLeases) lease.second.value->Release();
    modelLeases.clear();
}
void ModelDeactivateStepper() {
    if (!modelStepper) return;
    Require(modelStepper->Deactivate());
    modelStepper->Release();
    modelStepper = nullptr;
}
void ModelDeactivateBreakpoints() {
    ModelDeactivateStepper();
    for (auto& binding : modelBindings) {
        if (binding.breakpoint) { Require(binding.breakpoint->Activate(0)); binding.breakpoint->Release(); }
        binding.breakpoint = nullptr;
    }
}
void ModelCleanup() {
    modelBindings.clear();
    for (auto& entry : modelThreads) entry.second->Release();
    modelThreads.clear();
    for (auto& entry : modelModules) entry.first->Release();
    modelModules.clear();
    catalog = Json::array(); mobileEvents = Json::array();
}
void ModelThread(Unknown* thread, bool added) {
    uint32_t id = 0;
    Require(static_cast<Thread*>(thread)->GetID(&id));
    auto old = modelThreads.find(id);
    if (old != modelThreads.end()) { old->second->Release(); modelThreads.erase(old); }
    if (added) { thread->AddRef(); modelThreads.emplace(id, static_cast<Thread*>(thread)); }
    Event({{"Kind", added ? "thread" : "threadExited"}, {"Generation", NewId()},
        {"Thread", {{"Id", id}, {"Name", "Managed thread " + std::to_string(id)}}}});
}
void ModelModule(Unknown* value, bool added) {
    auto found = modelModules.find(value);
    if (!added) {
        if (found != modelModules.end()) {
            Event({{"Kind", "moduleRemoved"}, {"Generation", NewId()}, {"Module", found->second}});
            found->first->Release(); modelModules.erase(found);
        }
        return;
    }
    if (found != modelModules.end()) return;
    char16_t path[4096]{};
    uint32_t count = 0;
    Require(static_cast<Module*>(value)->GetName(4096, &count, path));
    if (count > 4096) throw std::runtime_error("ModuleNameLimit");
    std::string name = Utf8(path, count && path[count - 1] == 0 ? count - 1 : count);
    int64_t generation = NewId();
    Json module{{"Id", std::to_string(generation)}, {"Name", name.substr(name.find_last_of("/\\") + 1)},
        {"Path", name}, {"Mvid", ModuleMvid(static_cast<Module*>(value))}, {"Generation", generation}};
    value->AddRef();
    modelModules.emplace(value, module);
    Event({{"Kind", "module"}, {"Generation", generation}, {"Module", module}});
}
void ModelBind(Unknown* module) {
    if (modelBindings.empty()) return;
    std::string mvid = ModuleMvid(static_cast<Module*>(module));
    for (auto& binding : modelBindings) {
        if (binding.breakpoint || binding.location.at("Mvid") != mvid) continue;
        Ref<Function> function;
        Ref<Code> code;
        Ref<Breakpoint> breakpoint;
        Require(static_cast<Module*>(module)->GetFunctionFromToken(binding.location.at("MethodToken").get<uint32_t>(), &function.value));
        Require(function.value->GetILCode(&code.value));
        Require(code.value->CreateBreakpoint(binding.location.at("IlOffset").get<uint32_t>(), &breakpoint.value));
        Require(breakpoint.value->Activate(1));
        binding.breakpoint = breakpoint.value; breakpoint.value = nullptr;
        Event({{"Kind", "breakpoint"}, {"Generation", NewId()},
            {"Breakpoint", {{"Line", binding.line}, {"Verified", true}, {"Source", binding.source}}}});
    }
}
Json ModelSetBreakpoints(const Json& request) {
    if (debug && !stoppedController) throw std::runtime_error("TargetMustBeStopped");
    const auto& lines = request.at("Lines");
    const auto& locations = request.at("Locations");
    std::string source = request.at("Source").get<std::string>();
    if (!lines.is_array() || lines.size() > 256 || !locations.is_array() || lines.size() != locations.size() || source.size() > 8192)
        throw std::runtime_error("InvalidState");
    for (size_t i = 0; i < lines.size(); ++i) {
        const auto& item = locations[i];
        if (lines[i].get<int>() <= 0 || item.at("Mvid").get<std::string>().size() != 32 ||
            (item.at("MethodToken").get<uint32_t>() & 0xff000000) != 0x06000000 ||
            item.at("IlOffset").get<int64_t>() < 0 || item.at("IlOffset").get<int64_t>() > UINT32_MAX)
            throw std::runtime_error("InvalidState");
        bool matches = false;
        for (auto& module : catalog) if (module.at("Mvid") == item.at("Mvid"))
            for (auto& method : module.at("Methods")) if (method.at("Token") == item.at("MethodToken"))
                for (auto& point : method.at("Points"))
                    if (point.at("Offset") == item.at("IlOffset") && point.at("Source") == source && point.at("Line") == lines[i])
                        matches = true;
        if (!matches) throw std::runtime_error("SymbolIdentityMismatch");
    }
    for (auto it = modelBindings.begin(); it != modelBindings.end();) {
        if (it->source != source) { ++it; continue; }
        if (it->breakpoint) { Require(it->breakpoint->Activate(0)); it->breakpoint->Release(); }
        it = modelBindings.erase(it);
    }
    if (modelBindings.size() + lines.size() > 1024) throw std::runtime_error("BreakpointLimit");
    for (size_t i = 0; i < lines.size(); ++i) modelBindings.push_back({locations[i], source, lines[i].get<int>(), nullptr});
    for (auto& module : modelModules) ModelBind(module.first);
    Json result = Json::array();
    for (auto& binding : modelBindings) if (binding.source == source)
        result.push_back({{"Line", binding.line}, {"Verified", binding.breakpoint != nullptr}, {"Source", binding.source}});
    return result;
}
Json ModelStackFrame(Frame* frame, uint32_t token, uint32_t offset, int64_t handle) {
    Ref<Function> function;
    Ref<Module> module;
    Require(frame->GetFunction(&function.value)); Require(function.value->GetModule(&module.value));
    std::string mvid = ModuleMvid(module.value);
    Json method = MethodSymbols(frame), point = nullptr;
    if (!method.is_null())
        for (auto& candidate : method.at("Points"))
            if (candidate.at("Offset").get<uint32_t>() <= offset &&
                (point.is_null() || candidate.at("Offset").get<uint32_t>() > point.at("Offset").get<uint32_t>())) point = candidate;
    return {{"Handle", handle}, {"Name", method.is_null() ? Json("method " + std::to_string(token)) : method.at("Name")},
        {"Source", point.is_null() ? Json(nullptr) : point.at("Source")}, {"Line", point.is_null() ? Json(0) : point.at("Line")},
        {"StopId", stopId}, {"Mvid", mvid}, {"MethodToken", token}, {"IlOffset", offset}};
}
Json ModelFullStack() {
    Ref<ChainEnum> chains;
    Require(static_cast<Thread*>(stoppedThread)->EnumerateChains(reinterpret_cast<Unknown**>(&chains.value)));
    Json result = Json::array();
    for (int c = 0; c < 128 && result.size() < 128; ++c) {
        Ref<Chain> chain;
        uint32_t fetched = 0;
        Require(chains.value->Next(1, &chain.value, &fetched));
        if (!fetched) break;
        Bool managed = 0; Require(chain.value->IsManaged(&managed));
        if (!managed) continue;
        Ref<FrameEnum> entries;
        Require(chain.value->EnumerateFrames(&entries.value));
        while (result.size() < 128) {
            Ref<Frame> frame;
            Require(entries.value->Next(1, &frame.value, &fetched));
            if (!fetched) break;
            Ref<ILFrame> il;
            uint32_t token = 0, offset = 0, mapping = 0;
            if (frame.value->QueryInterface(ILFrameId, reinterpret_cast<void**>(&il.value)) < 0 ||
                frame.value->GetFunctionToken(&token) < 0 || il.value->GetIP(&offset, &mapping) < 0) continue;
            if (frames.size() + modelLeases.size() >= 4096) throw std::runtime_error("HandleLimit");
            int64_t handle = NewId();
            Json row = ModelStackFrame(frame.value, token, offset, handle);
            frame.value->AddRef(); frames.emplace(handle, frame.value);
            result.push_back(std::move(row));
        }
    }
    return result;
}
int64_t Lease(Unknown* value, bool scope = false, Json variables = nullptr) {
    if (modelLeases.size() + frames.size() >= 4096) throw std::runtime_error("HandleLimit");
    int64_t id = NewId();
    value->AddRef(); modelLeases.emplace(id, ModelLease{value, scope, std::move(variables)});
    return id;
}
void Dereference(Value* original, Ref<Value>& result) {
    Ref<ReferenceValue> reference;
    HResult hr = original->QueryInterface(ReferenceValueId, reinterpret_cast<void**>(&reference.value));
    if (hr >= 0 && reference.value) {
        Bool null = 0; Require(reference.value->IsNull(&null));
        if (!null) Require(reference.value->Dereference(&result.value));
    } else { original->AddRef(); result.value = original; }
}
Json ObjectSymbols(ObjectValue* object, Ref<Class>& klass) {
    Ref<Module> module;
    uint32_t token = 0;
    Require(object->GetClass(&klass.value)); Require(klass.value->GetModule(&module.value)); Require(klass.value->GetToken(&token));
    Json symbols = Symbols(module.value);
    if (!symbols.is_null()) for (auto& type : symbols.at("Types")) if (type.at("Token") == token) return type;
    return nullptr;
}
template<class T> std::string FloatingText(T value) {
    if (std::isnan(value)) return "NaN";
    if (std::isinf(value)) return std::signbit(value) ? "-Infinity" : "Infinity";
    std::ostringstream text;
    text.imbue(std::locale::classic());
    text << std::setprecision(std::numeric_limits<T>::max_digits10) << value;
    if (!text) throw std::runtime_error("ValueFormattingFailed");
    return text.str();
}
Json Describe(Value* original, const std::string& name = "") {
    Ref<Value> value;
    Dereference(original, value);
    Json result{{"Name", name}, {"Handle", 0}, {"Value", "null"}, {"Type", "object"}};
    if (!value.value) return result;
    uint32_t type = 0, size = 0;
    Require(value.value->GetType(&type)); Require(value.value->GetSize(&size));
    Ref<StringValue> text;
    if (value.value->QueryInterface(StringValueId, reinterpret_cast<void**>(&text.value)) >= 0 && text.value) {
        uint32_t count = 0, fetched = 0;
        Require(text.value->GetLength(&count));
        if (count > 4096) throw std::runtime_error("StringLimit");
        std::vector<char16_t> buffer(count + 1);
        Require(text.value->GetString(count + 1, &fetched, buffer.data()));
        if (fetched > count + 1) throw std::runtime_error("StringLimit");
        result["Value"] = Json(Utf8(buffer.data(), count)).dump(); result["Type"] = "string"; return result;
    }
    Ref<ArrayValue> array;
    if (value.value->QueryInterface(ArrayValueId, reinterpret_cast<void**>(&array.value)) >= 0 && array.value) {
        uint32_t count = 0, rank = 0;
        Require(array.value->GetRank(&rank)); Require(array.value->GetCount(&count));
        if (rank != 1) throw std::runtime_error("UnsupportedArrayRank");
        Bool lower = 0; Require(array.value->HasBaseIndicies(&lower));
        if (lower) {
            uint32_t index = 0; Require(array.value->GetBaseIndicies(1, &index));
            if (index != 0) throw std::runtime_error("UnsupportedArrayBounds");
        }
        result["Value"] = "array[" + std::to_string(count) + "]"; result["Type"] = "array";
        result["Handle"] = Lease(value.value); result["IndexedVariables"] = count; return result;
    }
    Ref<GenericValue> generic;
    if (type >= 2 && type <= 13 && size <= 8 && value.value->QueryInterface(GenericValueId, reinterpret_cast<void**>(&generic.value)) >= 0 && generic.value) {
        uint64_t bits = 0; Require(generic.value->GetValue(&bits));
        switch (type) {
            case 2: result["Value"] = bits ? "true" : "false"; result["Type"] = "bool"; break;
            case 3: { char16_t c = static_cast<char16_t>(bits); result["Value"] = Json(Utf8(&c, 1)).dump(); result["Type"] = "char"; break; }
            case 4: result["Value"] = std::to_string(static_cast<int8_t>(bits)); result["Type"] = "sbyte"; break;
            case 5: result["Value"] = std::to_string(static_cast<uint8_t>(bits)); result["Type"] = "byte"; break;
            case 6: result["Value"] = std::to_string(static_cast<int16_t>(bits)); result["Type"] = "short"; break;
            case 7: result["Value"] = std::to_string(static_cast<uint16_t>(bits)); result["Type"] = "ushort"; break;
            case 8: result["Value"] = std::to_string(static_cast<int32_t>(bits)); result["Type"] = "int"; break;
            case 9: result["Value"] = std::to_string(static_cast<uint32_t>(bits)); result["Type"] = "uint"; break;
            case 10: result["Value"] = std::to_string(static_cast<int64_t>(bits)); result["Type"] = "long"; break;
            case 11: result["Value"] = std::to_string(bits); result["Type"] = "ulong"; break;
            case 12: { float f; std::memcpy(&f, &bits, sizeof(f)); result["Value"] = FloatingText(f); result["Type"] = "float"; break; }
            case 13: { double d; std::memcpy(&d, &bits, sizeof(d)); result["Value"] = FloatingText(d); result["Type"] = "double"; break; }
        }
        return result;
    }
    Ref<ObjectValue> object;
    if (value.value->QueryInterface(ObjectValueId, reinterpret_cast<void**>(&object.value)) >= 0 && object.value) {
        Ref<Class> klass;
        Json symbols = ObjectSymbols(object.value, klass);
        result["Type"] = symbols.is_null() ? Json("object") : symbols.at("Name");
        result["Value"] = symbols.is_null() ? Json("<unavailable: matching type symbols required>") : symbols.at("Name");
        if (!symbols.is_null()) {
            result["Handle"] = Lease(value.value); result["NamedVariables"] = symbols.at("Fields").size();
        }
        return result;
    }
    throw std::runtime_error("UnsupportedValueType");
}
Json ModelScopes(int64_t id) {
    auto found = frames.find(id);
    if (!stoppedController || found == frames.end()) throw std::runtime_error("InvalidState");
    Ref<ILFrame> frame;
    Require(found->second->QueryInterface(ILFrameId, reinterpret_cast<void**>(&frame.value)));
    Json variables = VisibleVariables(frame.value);
    return Json::array({{{"Name", "Locals"}, {"Handle", Lease(frame.value, true, variables)}, {"Count", variables.size()}}});
}
void ChildValue(Value* value, const std::string& member, bool indexed, Ref<Value>& child) {
    Ref<Value> target;
    Dereference(value, target);
    if (!target.value) throw std::runtime_error("NullReference");
    if (indexed) {
        Ref<ArrayValue> array;
        Require(target.value->QueryInterface(ArrayValueId, reinterpret_cast<void**>(&array.value)));
        uint32_t rank = 0, count = 0;
        Require(array.value->GetRank(&rank)); Require(array.value->GetCount(&count));
        unsigned long index = std::stoul(member);
        if (rank != 1 || index >= count) throw std::runtime_error("InvalidIndex");
        Require(array.value->GetElementAtPosition(static_cast<uint32_t>(index), &child.value));
    } else {
        Ref<ObjectValue> object;
        Ref<Class> klass;
        Require(target.value->QueryInterface(ObjectValueId, reinterpret_cast<void**>(&object.value)));
        Json symbols = ObjectSymbols(object.value, klass);
        if (!symbols.is_null()) for (auto& field : symbols.at("Fields"))
            if (field.at("Name") == member) {
                Require(object.value->GetFieldValue(klass.value, field.at("Token").get<uint32_t>(), &child.value)); return;
            }
        throw std::runtime_error("FieldUnavailable");
    }
}
Json ModelVariables(const Json& request) {
    int64_t id = request.at("Handle").get<int64_t>();
    int start = request.value("Start", 0), count = request.value("Count", 0);
    if (!stoppedController || !modelLeases.count(id) || start < 0 || count < 0 || count > 1000)
        throw std::runtime_error("InvalidState");
    bool all = count == 0;
    if (all) count = 1000;
    std::string filter = request.contains("Filter") && !request.at("Filter").is_null() ? request.at("Filter").get<std::string>() : "";
    if (filter != "" && filter != "named" && filter != "indexed") throw std::runtime_error("InvalidState");
    const auto& lease = modelLeases.at(id);
    Json result = Json::array();
    if (lease.scope) {
        if (filter == "indexed") return result;
        if (all && lease.variables.size() > static_cast<size_t>(start) + count) throw std::runtime_error("VariableLimit");
        int index = 0;
        for (auto& variable : lease.variables) {
            if (index++ < start) continue;
            if (result.size() >= static_cast<size_t>(count)) break;
            Ref<Value> value;
            auto* frame = static_cast<ILFrame*>(lease.value);
            uint32_t slot = variable.at("Index").get<uint32_t>();
            if (variable.at("Parameter").get<bool>()) Require(frame->GetArgument(slot, &value.value));
            else Require(frame->GetLocalVariable(slot, &value.value));
            result.push_back(Describe(value.value, variable.at("Name").get<std::string>()));
        }
    } else {
        auto* value = static_cast<Value*>(lease.value);
        Ref<ArrayValue> array;
        bool indexed = value->QueryInterface(ArrayValueId, reinterpret_cast<void**>(&array.value)) >= 0 && array.value;
        if ((indexed && filter == "named") || (!indexed && filter == "indexed")) return result;
        if (indexed) {
            uint32_t size = 0; Require(array.value->GetCount(&size));
            if (all && size > static_cast<uint64_t>(start) + count) throw std::runtime_error("VariableLimit");
            for (uint32_t i = static_cast<uint32_t>(start); i < size && result.size() < static_cast<size_t>(count); ++i) {
                Ref<Value> child; Require(array.value->GetElementAtPosition(i, &child.value));
                result.push_back(Describe(child.value, "[" + std::to_string(i) + "]"));
            }
        } else {
            Ref<ObjectValue> object; Ref<Class> klass;
            Require(value->QueryInterface(ObjectValueId, reinterpret_cast<void**>(&object.value)));
            Json symbols = ObjectSymbols(object.value, klass);
            if (symbols.is_null()) throw std::runtime_error("SymbolsUnavailable");
            if (all && symbols.at("Fields").size() > static_cast<size_t>(start) + count) throw std::runtime_error("VariableLimit");
            int index = 0;
            for (auto& field : symbols.at("Fields")) {
                if (index++ < start) continue;
                if (result.size() >= static_cast<size_t>(count)) break;
                Ref<Value> child;
                Require(object.value->GetFieldValue(klass.value, field.at("Token").get<uint32_t>(), &child.value));
                result.push_back(Describe(child.value, field.at("Name").get<std::string>()));
            }
        }
    }
    return result;
}
Json ModelEvaluate(const Json& request) {
    int64_t id = request.at("Frame").get<int64_t>();
    if (!stoppedController || !frames.count(id)) throw std::runtime_error("InvalidState");
    std::string expression = request.at("Expression").get<std::string>();
    if (expression.size() > 256) throw std::runtime_error("UnsupportedExpression");
    size_t cursor = 0;
    auto space = [&] { while (cursor < expression.size() && expression[cursor] == ' ') ++cursor; };
    auto identifier = [&] {
        size_t begin = cursor;
        while (cursor < expression.size() && (std::isalnum(static_cast<unsigned char>(expression[cursor])) || expression[cursor] == '_')) ++cursor;
        if (begin == cursor || std::isdigit(static_cast<unsigned char>(expression[begin]))) throw std::runtime_error("UnsupportedExpression");
        return expression.substr(begin, cursor - begin);
    };
    space(); std::string name = identifier();
    Ref<ILFrame> frame; Ref<Value> value;
    Require(frames.at(id)->QueryInterface(ILFrameId, reinterpret_cast<void**>(&frame.value)));
    for (auto& variable : VisibleVariables(frame.value)) if (variable.at("Name") == name) {
        uint32_t slot = variable.at("Index").get<uint32_t>();
        if (variable.at("Parameter").get<bool>()) Require(frame.value->GetArgument(slot, &value.value));
        else Require(frame.value->GetLocalVariable(slot, &value.value));
        break;
    }
    if (!value.value) throw std::runtime_error("UnknownVariable");
    while (cursor < expression.size() && (expression[cursor] == '.' || expression[cursor] == '[')) {
        bool indexed = expression[cursor++] == '[';
        std::string member;
        if (indexed) {
            size_t begin = cursor;
            while (cursor < expression.size() && std::isdigit(static_cast<unsigned char>(expression[cursor]))) ++cursor;
            if (cursor == begin || cursor >= expression.size() || expression[cursor++] != ']') throw std::runtime_error("UnsupportedExpression");
            member = expression.substr(begin, cursor - begin - 1);
        } else member = identifier();
        Ref<Value> child; ChildValue(value.value, member, indexed, child);
        value.value->Release(); value.value = child.value; child.value = nullptr;
    }
    space();
    if (cursor == expression.size()) return Describe(value.value);
    char operation = expression[cursor++];
    if (operation != '+' && operation != '-') throw std::runtime_error("UnsupportedExpression");
    space(); size_t begin = cursor;
    if (cursor < expression.size() && (expression[cursor] == '-' || expression[cursor] == '+')) ++cursor;
    size_t digits = cursor;
    while (cursor < expression.size() && std::isdigit(static_cast<unsigned char>(expression[cursor]))) ++cursor;
    if (digits == cursor) throw std::runtime_error("UnsupportedExpression");
    std::string literal = expression.substr(begin, cursor - begin);
    space(); if (cursor != expression.size()) throw std::runtime_error("UnsupportedExpression");
    Json described = Describe(value.value);
    if (described.at("Type") != "int") throw std::runtime_error("UnsupportedExpression");
    int64_t left = std::stoll(described.at("Value").get<std::string>()), right = std::stoll(literal);
    if (right < INT32_MIN || right > INT32_MAX) throw std::runtime_error("IntegerOverflow");
    int64_t result = operation == '+' ? left + right : left - right;
    if (result < INT32_MIN || result > INT32_MAX) throw std::runtime_error("IntegerOverflow");
    return {{"Value", std::to_string(result)}, {"Type", "int"}, {"Handle", 0}};
}
Json StopDescription() {
    if (!stoppedThread || !stopId) throw std::runtime_error("InvalidState");
    uint32_t thread = 0; Require(static_cast<Thread*>(stoppedThread)->GetID(&thread));
    return {{"Id", stopId}, {"Reason", stopReason}, {"ThreadId", thread}};
}
Json ModelPoll() {
    Pump();
    if (stoppedController && !stopReported) {
        Event({{"Kind", "stopped"}, {"Generation", stopId}, {"Stop", StopDescription()}});
        stopReported = true;
    }
    if (exited.load()) {
        Event({{"Kind", "exited"}, {"Generation", NewId()}});
    }
    Json events = std::move(mobileEvents);
    mobileEvents = Json::array();
    return events;
}
void ModelStep(const Json& request) {
    if (!stopId || request.at("Stop") != stopId || request.at("ThreadId") != StopDescription().at("ThreadId"))
        throw std::runtime_error("InvalidState");
    std::string kind = request.at("Kind").get<std::string>();
    if (kind != "next" && kind != "stepIn" && kind != "stepOut") throw std::runtime_error("UnsupportedOperation");
    Ref<Frame> active; Ref<Stepper> stepper;
    Require(static_cast<Thread*>(stoppedThread)->GetActiveFrame(&active.value));
    Require(active.value->CreateStepper(reinterpret_cast<Unknown**>(&stepper.value)));
    Require(stepper.value->SetRangeIL(1)); Require(stepper.value->SetUnmappedStopMask(0)); Require(stepper.value->SetInterceptMask(0));
    if (kind == "stepOut") Require(stepper.value->StepOut());
    else {
        Ref<ILFrame> il; Require(active.value->QueryInterface(ILFrameId, reinterpret_cast<void**>(&il.value)));
        uint32_t offset = 0, mapping = 0; Require(il.value->GetIP(&offset, &mapping));
        Json method = MethodSymbols(active.value);
        uint32_t end = UINT32_MAX;
        if (!method.is_null()) for (auto& point : method.at("Points")) {
            uint32_t next = point.at("Offset").get<uint32_t>();
            if (next > offset && next < end) end = next;
        }
        if (end == UINT32_MAX) Require(stepper.value->Step(kind == "stepIn"));
        else { dncdbg::StepRange range{offset, end}; Require(stepper.value->StepRange(kind == "stepIn", &range, 1)); }
    }
    modelStepper = stepper.value; stepper.value = nullptr;
    ContinueStop();
}
void ModelPause() {
    if (!process || stoppedController) throw std::runtime_error("InvalidState");
    Require(process->Stop(5000));
    for (auto& entry : modelThreads) {
        Ref<Frame> active;
        if (entry.second->GetActiveFrame(&active.value) >= 0 && active.value) {
            stoppedThread = entry.second; stoppedThread->AddRef();
            stoppedController = process; stoppedController->AddRef();
            stopId = NewId(); stopReason = "pause"; stopReported = false;
            ModelDeactivateStepper(); return;
        }
    }
    Require(process->Continue(0));
    throw std::runtime_error("NoManagedThread");
}
std::string ModelExceptionType(Thread* thread) {
    Ref<Value> original, value;
    Require(thread->GetCurrentException(&original.value));
    Dereference(original.value, value);
    if (!value.value) throw std::runtime_error("ExceptionUnavailable");
    Ref<ObjectValue> object; Ref<Class> klass; Ref<Module> module; Ref metadata;
    Require(value.value->QueryInterface(ObjectValueId, reinterpret_cast<void**>(&object.value)));
    Require(object.value->GetClass(&klass.value)); Require(klass.value->GetModule(&module.value));
    uint32_t token = 0, count = 0, flags = 0, base = 0;
    char16_t name[1024]{};
    Require(klass.value->GetToken(&token)); Require(module.value->GetMetaDataInterface(MetadataId, &metadata.value));
    Require(static_cast<MetadataImport*>(metadata.value)->GetTypeDefProps(token, name, 1024, &count, &flags, &base));
    if (count > 1024) throw std::runtime_error("TypeNameLimit");
    return Utf8(name, count && name[count - 1] == 0 ? count - 1 : count);
}
void ModelExceptions(const Json& request) {
    const auto& rules = request.at("Rules");
    if (rules.size() > 64) throw std::runtime_error("InvalidState");
    std::vector<ExceptionRule> parsed;
    for (const auto& rule : rules) {
        std::string filter = rule.at("Filter").get<std::string>();
        if (filter != "all" && filter != "unhandled") throw std::runtime_error("UnsupportedOperation");
        std::string type;
        if (rule.contains("Type") && !rule.at("Type").is_null()) {
            type = rule.at("Type").get<std::string>();
            if (type.empty() || type.size() > 256 ||
                !(std::isalpha(static_cast<unsigned char>(type[0])) || type[0] == '_') ||
                !std::all_of(type.begin(), type.end(), [](unsigned char c) {
                    return std::isalnum(c) || c == '_' || c == '.' || c == '+' || c == '`';
                })) throw std::runtime_error("InvalidState");
        }
        parsed.push_back({filter == "all" ? CallbackKind::ExceptionThrown : CallbackKind::ExceptionUnhandled, std::move(type)});
    }
    exceptionRules = std::move(parsed);
}
bool ModelExceptionMatches(Unknown* thread, CallbackKind stage) {
    std::string type;
    for (const auto& rule : exceptionRules) {
        if (rule.stage != stage) continue;
        if (rule.type.empty()) return true;
        if (type.empty()) type = ModelExceptionType(static_cast<Thread*>(thread));
        if (rule.type == type) return true;
    }
    return false;
}
Json ModelException(int64_t id) {
    if (id != stopId || stopReason != "exception" || !stoppedThread) throw std::runtime_error("InvalidState");
    std::string type = ModelExceptionType(static_cast<Thread*>(stoppedThread));
    return {{"Type", type}, {"Description", type + " (message/property func-eval is unavailable in this provider)"},
        {"BreakMode", exceptionBreakMode}};
}
void ModelConfigure(const Json& request) {
    bool append = request.value("Append", false);
    if (debug || (!catalog.empty() && !append)) throw std::runtime_error("InvalidState");
    const auto& modules = request.at("Modules");
    if (!modules.is_array() || modules.size() > 256) throw std::runtime_error("InvalidState");
    for (auto& module : modules) {
        std::string mvid = module.at("Mvid").get<std::string>();
        if (mvid.size() != 32 || !std::all_of(mvid.begin(), mvid.end(), [](unsigned char c) { return std::isxdigit(c); }) ||
            !module.at("Methods").is_array() || module.at("Methods").size() > 65536 ||
            !module.at("Types").is_array() || module.at("Types").size() > 65536)
            throw std::runtime_error("InvalidState");
        for (auto& method : module.at("Methods")) {
            if ((method.at("Token").get<uint32_t>() & 0xff000000) != 0x06000000 ||
                method.at("Name").get<std::string>().size() > 8192 || !method.at("Variables").is_array() ||
                method.at("Variables").size() > 1024 || !method.at("Points").is_array())
                throw std::runtime_error("InvalidState");
            for (auto& variable : method.at("Variables"))
                if (variable.at("Name").get<std::string>().size() > 256 || !variable.at("Parameter").is_boolean() ||
                    variable.at("Index").get<int>() < 0 || variable.at("Index").get<int>() > 65535 ||
                    variable.value("StartOffset", int64_t{0}) < 0 ||
                    variable.value("EndOffset", int64_t{INT32_MAX}) < variable.value("StartOffset", int64_t{0}))
                    throw std::runtime_error("InvalidState");
            for (auto& point : method.at("Points"))
                if (point.at("Offset").get<int64_t>() < 0 || point.at("Offset").get<int64_t>() > UINT32_MAX ||
                    point.at("Source").get<std::string>().size() > 8192 || point.at("Line").get<int>() <= 0 ||
                    point.at("Checksum").get<std::string>().size() > 128 || point.at("Algorithm").get<std::string>().size() > 32)
                    throw std::runtime_error("InvalidState");
        }
        for (auto& type : module.at("Types")) {
            if ((type.at("Token").get<uint32_t>() & 0xff000000) != 0x02000000 ||
                type.at("Name").get<std::string>().size() > 8192 || !type.at("Fields").is_array() || type.at("Fields").size() > 4096)
                throw std::runtime_error("InvalidState");
            for (auto& field : type.at("Fields"))
                if ((field.at("Token").get<uint32_t>() & 0xff000000) != 0x04000000 ||
                    field.at("Name").get<std::string>().size() > 1024) throw std::runtime_error("InvalidState");
        }
    }
    Json merged = catalog;
    for (auto& module : modules) {
        bool found = false;
        for (auto& old : merged) if (old.at("Mvid") == module.at("Mvid")) {
            if (old.at("Name") != module.at("Name")) throw std::runtime_error("SymbolIdentityMismatch");
            for (const char* collection : {"Methods", "Types"})
                for (auto& item : module.at(collection)) {
                    bool exists = false;
                    for (auto& existing : old.at(collection))
                        if (existing.at("Token") == item.at("Token")) {
                            if (std::strcmp(collection, "Methods") || existing.at("Name") != item.at("Name") ||
                                existing.at("Variables") != item.at("Variables")) throw std::runtime_error("DuplicateSymbolToken");
                            for (auto& point : item.at("Points")) {
                                for (auto& previous : existing.at("Points"))
                                    if (previous.at("Offset") == point.at("Offset")) throw std::runtime_error("DuplicateSymbolToken");
                                existing["Points"].push_back(point);
                            }
                            exists = true; break;
                        }
                    if (!exists) old[collection].push_back(item);
                }
            found = true; break;
        }
        if (!found) merged.push_back(module);
    }
    if (merged.size() > 256 || merged.dump().size() > 16 * 1024 * 1024) throw std::runtime_error("SymbolLimit");
    catalog = std::move(merged);
}
