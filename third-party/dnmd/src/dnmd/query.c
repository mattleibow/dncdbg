#include "internal.h"

mdcursor_t create_cursor(mdtable_t* table, uint32_t row)
{
    assert(table != NULL && row <= (table->row_count + 1));
    mdcursor_t c;
    c._reserved1 = (intptr_t)table;
    c._reserved2 = row;
    return c;
}

static mdtable_t* type_to_table(mdcxt_t* cxt, mdtable_id_t table_id)
{
    assert(cxt != NULL);
    assert(0 <= table_id && table_id < MDTABLE_MAX_COUNT);
    return &cxt->tables[table_id];
}

bool md_create_cursor(mdhandle_t handle, mdtable_id_t table_id, mdcursor_t* cursor, uint32_t* count)
{
    if (count == NULL)
        return false;

    // Set the token to the first row.
    // If the table is empty, the call will return false.
    if (!md_token_to_cursor(handle, (CreateTokenType(table_id) | 1), cursor))
        return false;

    *count = CursorTable(cursor)->row_count;
    return true;
}

static bool cursor_move_no_checks(mdcursor_t* c, int32_t delta)
{
    assert(c != NULL);

    mdtable_t* table = CursorTable(c);
    uint32_t row = CursorRow(c);
    row += delta;
    // Indices into tables begin at 1 - see II.22.
    // They can also point to index n+1, which
    // indicates the end.
    if (row == 0 || row > (table->row_count + 1))
        return false;

    *c = create_cursor(table, row);
    return true;
}

bool md_cursor_move(mdcursor_t* c, int32_t delta)
{
    if (c == NULL || CursorTable(c) == NULL)
        return false;
    return cursor_move_no_checks(c, delta);
}

bool md_token_to_cursor(mdhandle_t handle, mdToken tk, mdcursor_t* c)
{
    assert(c != NULL);

    mdcxt_t* cxt = extract_mdcxt(handle);
    if (cxt == NULL)
        return false;

    mdtable_id_t table_id = ExtractTokenType(tk);
    if (table_id >= MDTABLE_MAX_COUNT)
        return false;

    mdtable_t* table = type_to_table(cxt, table_id);

    // Indices into tables begin at 1 - see II.22.
    uint32_t row = RidFromToken(tk);
    if (row == 0 || row > table->row_count)
        return false;

    *c = create_cursor(table, row);
    return true;
}

bool md_cursor_to_token(mdcursor_t c, mdToken* tk)
{
    assert(tk != NULL);

    mdtable_t* table = CursorTable(&c);
    if (table == NULL)
    {
        *tk = mdTokenNil;
        return true;
    }

    mdToken row = RidFromToken(CursorRow(&c));
    *tk = CreateTokenType(table->table_id) | row;
    // We'll allow getting a token for a cursor just past the end of the table.
    // These tokens are used in some scenarios to represent an empty list.
    return (row <= table->row_count + 1);
}

mdhandle_t md_extract_handle_from_cursor(mdcursor_t c)
{
    mdtable_t* table = CursorTable(&c);
    if (table == NULL)
        return NULL;
    return table->cxt;
}


static bool get_column_value_as_token_or_cursor(mdcursor_t* c, uint32_t col_idx, mdToken* tk, mdcursor_t* cursor)
{
    assert(c != NULL && (tk != NULL || cursor != NULL));

    access_cxt_t acxt;
    if (!create_access_context(c, col_idx, &acxt))
        return false;

    // If this isn't an index column, then fail.
    if (!(acxt.col_details & (mdtc_idx_table | mdtc_idx_coded)))
        return false;

    uint32_t table_row;
    mdtable_id_t table_id;

    uint32_t raw;

    if (!read_column_data(&acxt, &raw))
        return false;

    if (acxt.col_details & mdtc_idx_table)
    {
        // The raw value is the row index into the table that
        // is embedded in the column details.
        table_row = RidFromToken(raw);
        table_id = ExtractTable(acxt.col_details);
    }
    else
    {
        assert(acxt.col_details & mdtc_idx_coded);
        if (!decompose_coded_index(raw, acxt.col_details, &table_id, &table_row))
            return false;
    }

    if (0 > table_id || table_id >= MDTABLE_MAX_COUNT)
        return false;

    mdtable_t* table;
    if (tk != NULL)
    {
        *tk = CreateTokenType(table_id) | table_row;
    }
    else
    {
        // Returning a cursor means pointing directly into a table,
        // so we must validate the row prior to creating the cursor.
        table = type_to_table(acxt.table->cxt, table_id);

        // Indices into tables begin at 1 - see II.22.
        // However, tables can contain a row ID of 0 to
        // indicate "none" or point 1 past the end.
        if (table_row > table->row_count + 1)
            return false;

        // Sometimes we can get an index into a table of 0 or 1 past the end
        // of a table that does not exist. In that case, our table object here
        // will be completely uninitialized. Set the table id so we can do operations
        // that need a table id, like creating the table or getting a token.
        if (table->table_id == 0 && table_id != 0)
        {
            assert(table_row == 0 || table_row == 1);
            table->table_id = table_id;
        }

        assert(cursor != NULL);
        *cursor = create_cursor(table, table_row);
    }

    return true;
}

bool md_get_column_value_as_token(mdcursor_t c, col_index_t col_idx, mdToken* tk)
{
    assert(tk != NULL);
    return get_column_value_as_token_or_cursor(&c, col_idx, tk, NULL);
}

bool md_get_column_value_as_cursor(mdcursor_t c, col_index_t col_idx, mdcursor_t* cursor)
{
    assert(cursor != NULL);
    return get_column_value_as_token_or_cursor(&c, col_idx, NULL, cursor);
}

bool md_get_column_value_as_range(mdcursor_t c, col_index_t col_idx, mdcursor_t* cursor, uint32_t* count)
{
    assert(cursor != NULL);
    if (!get_column_value_as_token_or_cursor(&c, col_idx, NULL, cursor))
        return false;

    // Check if the cursor is null or the end of the table
    if (CursorNull(cursor) || CursorEnd(cursor))
    {
        *count = 0;
        return true;
    }

    mdcursor_t nextMaybe = c;
    // Loop here as we may have a bunch of null cursors in the column.
    for (;;)
    {
        // The cursor into the current table remains valid,
        // we can safely move it at least one beyond the last.
        (void)cursor_move_no_checks(&nextMaybe, 1);

        // Check if we are at the end of the current table. If so,
        // ECMA states we use the remaining rows in the target table.
        if (CursorEnd(&nextMaybe))
        {
            // Add +1 for inclusive count.
            *count = CursorTable(cursor)->row_count - CursorRow(cursor) + 1;
        }
        else
        {
            // Examine the current table's next row value to find the
            // extrema of the target table range.
            mdcursor_t end;
            if (!md_get_column_value_as_cursor(nextMaybe, col_idx, &end))
                return false;

            // The next row is a null cursor, which means we need to
            // check the next row in the current table.
            if (CursorNull(&end))
                continue;
            *count = CursorRow(&end) - CursorRow(cursor);
        }
        break;
    }

    return true;
}

bool md_get_column_value_as_constant(mdcursor_t c, col_index_t col_idx, uint32_t* constant)
{
    assert(constant != NULL);

    access_cxt_t acxt;
    if (!create_access_context(&c, col_idx, &acxt))
        return false;

    // If this isn't a constant column, then fail.
    if (!(acxt.col_details & mdtc_constant))
        return false;

    if (!read_column_data(&acxt, constant))
        return false;

    return true;
}

bool md_get_column_value_as_utf8(mdcursor_t c, col_index_t col_idx, char const** str)
{
    access_cxt_t acxt;
    if (!create_access_context(&c, col_idx, &acxt))
        return false;

    // If this isn't a heap index column, then fail.
    if (!(acxt.col_details & mdtc_hstring))
        return false;

    uint32_t offset;
    if (!read_column_data(&acxt, &offset))
        return false;

    if (!try_get_string(CursorTable(&c)->cxt, offset, str))
        return false;

    return true;
}

bool md_get_column_value_as_blob(mdcursor_t c, col_index_t col_idx, uint8_t const** blob, uint32_t* blob_len)
{
    assert(blob != NULL && blob_len != NULL);

    access_cxt_t acxt;
    if (!create_access_context(&c, col_idx, &acxt))
        return false;

    // If this isn't a heap index column, then fail.
    if (!(acxt.col_details & mdtc_hblob))
        return false;

    uint32_t offset;
    if (!read_column_data(&acxt, &offset))
        return false;

    if (!try_get_blob(CursorTable(&c)->cxt, offset, blob, blob_len))
        return false;

    return true;
}

bool md_get_column_value_as_guid(mdcursor_t c, col_index_t col_idx, mdguid_t* guid)
{
    assert(guid != NULL);

    access_cxt_t acxt;
    if (!create_access_context(&c, col_idx, &acxt))
        return false;

    // If this isn't a heap index column, then fail.
    if (!(acxt.col_details & mdtc_hguid))
        return false;

    uint32_t offset;
    if (!read_column_data(&acxt, &offset))
        return false;

    if (!try_get_guid(CursorTable(&c)->cxt, offset, guid))
        return false;

    return true;
}
