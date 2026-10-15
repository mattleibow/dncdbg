#include "internal.h"

md_blob_parse_result_t md_parse_document_name(mdhandle_t handle, uint8_t const* blob, size_t blob_len, char const* name, size_t* name_len)
{
    mdcxt_t* cxt = extract_mdcxt(handle);
    if (cxt == NULL)
        return mdbpr_InvalidArgument;

    if (blob == NULL || name_len == NULL)
        return mdbpr_InvalidArgument;

    // Only support one-character ASCII separators.
    // System.Reflection.Metadata.MetadataReader has the same limitation
    uint8_t separator;
    if (!read_u8(&blob, &blob_len, &separator))
        return mdbpr_InvalidBlob;

    if (separator > 0x7f)
        return mdbpr_InvalidBlob;

    uint8_t* name_current = (uint8_t*)name;
    size_t remaining_name_len = *name_len;
    size_t required_len = 0;
    md_blob_parse_result_t result = mdbpr_Success;
    bool write_separator = false;
    while (blob_len > 0)
    {
        if (write_separator)
        {
            // Add the required space for the separator.
            // If there is space in the buffer, write the separator.
            required_len += 1;
            if (name_current == NULL || remaining_name_len == 0)
            {
                result = mdbpr_InsufficientBuffer;
            }
            else
            {
                write_u8(&name_current, &remaining_name_len, separator);
            }
        }
        write_separator = separator != 0;

        // Get the next part of the path.
        uint32_t part_offset;
        if (!decompress_u32(&blob, &blob_len, &part_offset))
            return mdbpr_InvalidBlob;

        // The part blob is a UTF-8 string that is not null-terminated.
        const uint8_t* part;
        uint32_t part_len;
        if (!try_get_blob(cxt, part_offset, &part, &part_len))
            return mdbpr_InvalidBlob;

        // Add the required space for the part.
        // If there is space in the buffer, write the part.
        required_len += part_len;
        if (name_current == NULL || remaining_name_len < part_len)
        {
            result = mdbpr_InsufficientBuffer;
            continue;
        }
        else
        {
            memcpy(name_current, part, part_len);
            bool success = advance_output_stream(&name_current, &remaining_name_len, part_len);
            assert(success);
            (void)success;
        }
    }

    // Add the null terminator.
    required_len++;
    if (name_current != NULL && remaining_name_len > 0)
        write_u8(&name_current, &remaining_name_len, 0);
    else
        result = mdbpr_InsufficientBuffer;

    *name_len = required_len;
    return result;
}

// We only support up to UINT32_MAX - 1 sequence points per method.
// Technically, the number of supported sequence points in the spec is unbounded.
// However, the PE format that an ECMA-335 blob is commonly wrapped in
// can only support up to 4GB files, so we can't possibly have UINT32_MAX - 1 entries
// in any existing scenario anyway.
static uint32_t get_num_sequence_points(mdcursor_t method_debug_information, uint8_t const* blob, size_t blob_len)
{
    uint32_t num_records = 0;
    uint32_t ignored;
    if (!decompress_u32(&blob, &blob_len, &ignored)) // header LocalSignature
        return UINT32_MAX;

    mdcursor_t document;
    if (!md_get_column_value_as_cursor(method_debug_information, mdtMethodDebugInformation_Document, &document))
        return UINT32_MAX;

    if (CursorNull(&document) && !decompress_u32(&blob, &blob_len, &ignored)) // header InitialDocument
        return UINT32_MAX;

    bool first_record = true;
    while (blob_len > 0)
    {
        if (num_records == UINT32_MAX)
            return UINT32_MAX;
        num_records++;
        uint32_t il_offset;
        if (!decompress_u32(&blob, &blob_len, &il_offset)) // ILOffset
            return UINT32_MAX;

        // The first record cannot be a document record
        if (!first_record && il_offset == 0)
        {
            uint32_t document_offset;
            if (!decompress_u32(&blob, &blob_len, &document_offset)) // Document
                return UINT32_MAX;
        }
        else
        {
            // We don't need to check if we need to do an unsigned or signed decompression
            // as we will always read the same number of bytes and we don't care about the values here
            // as we're only calculating the number of records.
            uint32_t delta_lines;
            if (!decompress_u32(&blob, &blob_len, &delta_lines)) // DeltaLines
                return UINT32_MAX;

            uint32_t delta_columns;
            if (!decompress_u32(&blob, &blob_len, &delta_columns)) // DeltaColumns
                return UINT32_MAX;
            if (delta_lines != 0 || delta_columns != 0)
            {
                uint32_t start_line;
                if (!decompress_u32(&blob, &blob_len, &start_line)) // StartLine
                    return UINT32_MAX;
                uint32_t start_column;
                if (!decompress_u32(&blob, &blob_len, &start_column)) // StartColumn
                    return UINT32_MAX;
            }
        }

        first_record = false;
    }

    return num_records;
}

md_blob_parse_result_t md_parse_sequence_points(
    mdcursor_t method_debug_information,
    uint8_t const* blob,
    size_t blob_len,
    md_sequence_points_t* sequence_points,
    size_t* buffer_len)
{
    if (CursorNull(&method_debug_information) || CursorEnd(&method_debug_information))
        return mdbpr_InvalidArgument;

    if (blob == NULL || buffer_len == NULL)
        return mdbpr_InvalidArgument;

    uint32_t num_records = get_num_sequence_points(method_debug_information, blob, blob_len);

    if (num_records == UINT32_MAX)
        return mdbpr_InvalidBlob;

    size_t records_size;
    size_t required_size;
    if (!safe_mul_size(num_records, sizeof(sequence_points->records[0]), &records_size)
        || !safe_add_size(sizeof(md_sequence_points_t), records_size, &required_size))
    {
        return mdbpr_InvalidBlob;
    }
    if (sequence_points == NULL || *buffer_len < required_size)
    {
        *buffer_len = required_size;
        return mdbpr_InsufficientBuffer;
    }

    // header LocalSignature
    if (!decompress_u32(&blob, &blob_len, &sequence_points->signature))
        return mdbpr_InvalidBlob;

    mdcursor_t document;
    if (!md_get_column_value_as_cursor(method_debug_information, mdtMethodDebugInformation_Document, &document))
        return mdbpr_InvalidBlob;

    mdcxt_t* cxt = extract_mdcxt(md_extract_handle_from_cursor(method_debug_information));

    // header InitialDocument
    // Per the Portable PDB spec, the initial document is determined by
    // the Document column of the MethodDebugInformation row. When the
    // column is null (zero), the initial document RID is encoded in the
    // sequence points blob instead.
    if (!CursorNull(&document))
    {
        if (CursorEnd(&document))
            return mdbpr_InvalidBlob;
        sequence_points->document = document;
    }
    else
    {
        uint32_t document_rid;
        if (!decompress_u32(&blob, &blob_len, &document_rid))
            return mdbpr_InvalidBlob;

        if (document_rid != 0)
        {
            if (!md_token_to_cursor(cxt, CreateTokenType(mdtid_Document) | document_rid, &sequence_points->document))
                return mdbpr_InvalidBlob;
        }
        else
        {
            // No document — initialize to a null-like cursor.
            sequence_points->document = create_cursor(&cxt->tables[mdtid_Document], 0);
        }
    }

    // Accumulate delta-encoded values into running sums per the Portable PDB spec.
    // The blob encodes ILOffset, StartLine, and StartColumn as deltas from
    // the previous record (absolute for the first). The "rolling_" output
    // fields provide the accumulated (absolute) values to the caller.
    uint32_t running_il = 0;
    int64_t running_line = 0;
    int64_t running_col = 0;
    bool seen_non_hidden_sequence_point = false;
    for (uint32_t i = 0; blob_len > 0 && i < num_records; ++i)
    {
        uint32_t il_offset;
        if (!decompress_u32(&blob, &blob_len, &il_offset)) // ILOffset
            return mdbpr_InvalidBlob;

        // Check if the method transitioned
        // into a new source file.
        if (i != 0 && il_offset == 0)
        {
            uint32_t document_row_id;
            if (!decompress_u32(&blob, &blob_len, &document_row_id)) // Document
                return mdbpr_InvalidBlob;

            sequence_points->records[i].kind = mdsp_DocumentRecord;
            if (!md_token_to_cursor(cxt, CreateTokenType(mdtid_Document) | document_row_id, &sequence_points->records[i].document.document))
                return mdbpr_InvalidBlob;

            continue;
        }

        if (il_offset > UINT32_MAX - running_il)
            return mdbpr_InvalidBlob;
        running_il += il_offset;

        uint32_t delta_lines;
        if (!decompress_u32(&blob, &blob_len, &delta_lines)) // DeltaLines
            return mdbpr_InvalidBlob;

        int64_t delta_columns;
        if (delta_lines == 0)
        {
            uint32_t raw_delta_columns;
            if (!decompress_u32(&blob, &blob_len, &raw_delta_columns)) // DeltaColumns
                return mdbpr_InvalidBlob;
            delta_columns = raw_delta_columns;
        }
        else
        {
            int32_t raw_delta_columns;
            if (!decompress_i32(&blob, &blob_len, &raw_delta_columns)) // DeltaColumns
                return mdbpr_InvalidBlob;
            delta_columns = raw_delta_columns;
        }

        // Check for hidden point
        if (delta_lines == 0 && delta_columns == 0)
        {
            sequence_points->records[i].kind = mdsp_HiddenSequencePointRecord;
            sequence_points->records[i].hidden_sequence_point.rolling_il_offset = running_il;
            continue;
        }

        if (!seen_non_hidden_sequence_point)
        {
            seen_non_hidden_sequence_point = true;
            uint32_t start_line_raw;
            if (!decompress_u32(&blob, &blob_len, &start_line_raw)) // StartLine
                return mdbpr_InvalidBlob;
            uint32_t start_column_raw;
            if (!decompress_u32(&blob, &blob_len, &start_column_raw)) // StartColumn
                return mdbpr_InvalidBlob;
            running_line = start_line_raw;
            running_col = start_column_raw;
        }
        else
        {
            // Subsequent non-hidden records encode signed deltas.
            int32_t start_line_raw;
            if (!decompress_i32(&blob, &blob_len, &start_line_raw)) // DeltaStartLine
                return mdbpr_InvalidBlob;
            int32_t start_column_raw;
            if (!decompress_i32(&blob, &blob_len, &start_column_raw)) // DeltaStartColumn
                return mdbpr_InvalidBlob;
            running_line += start_line_raw;
            running_col += start_column_raw;
        }

        sequence_points->records[i].kind = mdsp_SequencePointRecord;
        sequence_points->records[i].sequence_point.rolling_il_offset = running_il;
        sequence_points->records[i].sequence_point.delta_lines = delta_lines;
        sequence_points->records[i].sequence_point.delta_columns = delta_columns;
        sequence_points->records[i].sequence_point.rolling_start_line = running_line;
        sequence_points->records[i].sequence_point.rolling_start_column = running_col;
    }

    if (blob_len != 0)
        return mdbpr_InvalidBlob;

    sequence_points->record_count = num_records;
    return mdbpr_Success;
}

// We only support up to UINT32_MAX - 1 imports per Imports blob.
// Technically, the number of supported imports in the spec is unbounded.
// However, the PE format that an ECMA-335 blob is commonly wrapped in
// can only support up to 4GB files, so we can't possibly have UINT32_MAX - 1 entries
// in any existing scenario anyway.
static uint32_t get_num_imports(uint8_t const* blob, size_t blob_len)
{
    uint32_t num_imports = 0;
    for (;blob_len > 0 && num_imports < UINT32_MAX; ++num_imports)
    {
        uint8_t kind;
        if (!read_u8(&blob, &blob_len, &kind))
            return UINT32_MAX;

        uint32_t raw;
        switch (kind)
        {
            case mdidk_ImportNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-namespace
                    return UINT32_MAX;
                break;
            case mdidk_ImportAssemblyNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-assembly
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-namespace
                    return UINT32_MAX;
                break;
            case mdidk_ImportType:
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-type
                    return UINT32_MAX;
                break;
            case mdidk_AliasNamespace:
            case mdidk_ImportXmlNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw)) // alias
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-namespace
                    return UINT32_MAX;
                break;
            case mdidk_ImportAssemblyReferenceAlias:
                if (!decompress_u32(&blob, &blob_len, &raw)) // alias
                    return UINT32_MAX;
                break;
            case mdidk_AliasAssemblyReference:
                if (!decompress_u32(&blob, &blob_len, &raw)) // alias
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-assembly
                    return UINT32_MAX;
                break;
            case mdidk_AliasAssemblyNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw)) // alias
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-assembly
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-namespace
                    return UINT32_MAX;
                break;
            case mdidk_AliasType:
                if (!decompress_u32(&blob, &blob_len, &raw)) // alias
                    return UINT32_MAX;
                if (!decompress_u32(&blob, &blob_len, &raw)) // target-type
                    return UINT32_MAX;
                break;
            default:
                return UINT32_MAX;
        }
    }
    return num_imports;
}

md_blob_parse_result_t md_parse_imports(mdhandle_t handle, uint8_t const* blob, size_t blob_len, md_imports_t* imports, size_t* buffer_len)
{
    mdcxt_t* cxt = extract_mdcxt(handle);
    if (cxt == NULL || blob == NULL || buffer_len == NULL)
        return mdbpr_InvalidArgument;

    uint32_t num_imports = get_num_imports(blob, blob_len);
    if (num_imports == UINT32_MAX)
        return mdbpr_InvalidBlob;

    size_t imports_size;
    size_t required_size;
    if (!safe_mul_size(num_imports, sizeof(imports->imports[0]), &imports_size)
        || !safe_add_size(sizeof(md_imports_t), imports_size, &required_size))
    {
        return mdbpr_InvalidBlob;
    }
    if (imports == NULL || *buffer_len < required_size)
    {
        *buffer_len = required_size;
        return mdbpr_InsufficientBuffer;
    }

    imports->count = num_imports;
    for (uint32_t i = 0; i < num_imports; ++i)
    {
        uint8_t kind;
        if (!read_u8(&blob, &blob_len, &kind))
            return mdbpr_InvalidBlob;

        // Zero out this import entry.
        memset(&imports->imports[i], 0, sizeof(imports->imports[i]));

        imports->imports[i].kind = kind;
        uint32_t raw;
        switch (kind)
        {
            case mdidk_ImportNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].target_namespace, &imports->imports[i].target_namespace_len))
                    return mdbpr_InvalidBlob;
                break;
            case mdidk_ImportAssemblyNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                imports->imports[i].assembly = CreateTokenType(mdtid_AssemblyRef) | raw;

                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].target_namespace, &imports->imports[i].target_namespace_len))
                    return mdbpr_InvalidBlob;
                break;
            case mdidk_ImportType:
            {
                mdtable_id_t table;
                uint32_t row_id;
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!decompose_coded_index(raw, mdtc_idx_coded | InsertCodedIndex(mdci_TypeDefOrRef), &table, &row_id))
                    return mdbpr_InvalidBlob;

                imports->imports[i].target_type = CreateTokenType(table) | row_id;
                break;
            }
            case mdidk_AliasNamespace:
            case mdidk_ImportXmlNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].alias, &imports->imports[i].alias_len))
                    return mdbpr_InvalidBlob;

                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].target_namespace, &imports->imports[i].target_namespace_len))
                    return mdbpr_InvalidBlob;
                break;
            case mdidk_ImportAssemblyReferenceAlias:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].alias, &imports->imports[i].alias_len))
                    return mdbpr_InvalidBlob;
                break;
            case mdidk_AliasAssemblyReference:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].alias, &imports->imports[i].alias_len))
                    return mdbpr_InvalidBlob;

                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                imports->imports[i].assembly = CreateTokenType(mdtid_AssemblyRef) | raw;
                break;
            case mdidk_AliasAssemblyNamespace:
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].alias, &imports->imports[i].alias_len))
                    return mdbpr_InvalidBlob;

                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                imports->imports[i].assembly = CreateTokenType(mdtid_AssemblyRef) | raw;

                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].target_namespace, &imports->imports[i].target_namespace_len))
                    return mdbpr_InvalidBlob;
                break;
            case mdidk_AliasType:
            {
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!try_get_blob(cxt, raw, (uint8_t const**)&imports->imports[i].alias, &imports->imports[i].alias_len))
                    return mdbpr_InvalidBlob;

                mdtable_id_t table;
                uint32_t row_id;
                if (!decompress_u32(&blob, &blob_len, &raw))
                    return mdbpr_InvalidBlob;

                if (!decompose_coded_index(raw, mdtc_idx_coded | InsertCodedIndex(mdci_TypeDefOrRef), &table, &row_id))
                    return mdbpr_InvalidBlob;

                imports->imports[i].target_type = CreateTokenType(table) | row_id;
                break;
            }
            default:
                return mdbpr_InvalidBlob;
        }
    }
    return mdbpr_Success;
}
