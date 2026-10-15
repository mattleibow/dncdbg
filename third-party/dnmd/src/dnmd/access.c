#include "internal.h"

bool create_access_context(mdcursor_t* cursor, col_index_t col_idx, access_cxt_t* acxt)
{
    mdtable_t* table = CursorTable(cursor);
    if (table == NULL)
        return false;

    uint32_t row = CursorRow(cursor);
    if (row == 0 || row > table->row_count)
        return false;

    uint8_t idx = col_to_index(col_idx, table);
    assert(idx < table->column_count);

    // Metadata row indexing is 1-based.
    row--;

    mdtcol_t col = table->column_details[idx];

    uint32_t offset_to_table_data = row * table->row_size_bytes + ExtractOffset(col);
#ifndef NDEBUG
    size_t len = (col & mdtc_b2) ? 2 : 4;
    assert(offset_to_table_data + len <= table->data.size);
#endif

    acxt->table = table;
    acxt->data = table->data.ptr + offset_to_table_data;
    acxt->col_details = col;
    return true;
}

bool read_column_data(access_cxt_t* acxt, uint32_t* data)
{
    assert(acxt != NULL && acxt->data != NULL && data != NULL);

    uint8_t const* table_data = acxt->data;

    if ((acxt->col_details & mdtc_b4) == mdtc_b4)
    {
        size_t len = 4;
        return read_u32(&table_data, &len, data);
    }
    else
    {
        size_t len = 2;
        uint16_t value;
        if (!read_u16(&table_data, &len, &value))
            return false;

        *data = value;
        return true;
    }
}
