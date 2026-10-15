#include "internal.h"

// mdlib magic number for context
#define MDLIB_MAGIC_NUMBER 0x3d71b

// Defined in II.24.2.1
#define METADATA_SIG 0x424A5342

static mdcxt_t* allocate_full_context(mdcxt_t* cxt)
{
    // The intent here is to call the allocator once.
    // Therefore we compute the full size and then call
    // malloc a single time. The following needs to be
    // done:
    //  1. Compute total amount of needed memory:
    //     - sizeof(mdcxt_t)
    //     - table count
    //     - column count for each table
    //  2. Copy supplied mdcxt_t to the newly allocated one
    //  3. Determine table array offset
    //  4. Set table pointer in mdcxt_t
    //  5. Determine column details array offsets
    //  6. Set column details array in each table
    //  7. Return the newly allocated context

    uint32_t table_col_sizes[MDTABLE_MAX_COUNT];
    uint32_t total_col_size = 0;
    for (mdtable_id_t id = mdtid_First; id < mdtid_End; ++id)
    {
        table_col_sizes[id] = sizeof(mdtcol_t) * get_table_column_count(id);
        total_col_size += table_col_sizes[id];
    }

    // Ensure all sections of the allocation are pointer aligned.
    size_t cxt_mem = align_to(sizeof(mdcxt_t), sizeof(void*));
    size_t tables_mem = MDTABLE_MAX_COUNT * align_to(sizeof(mdtable_t), sizeof(void*));
    size_t col_mem = align_to(total_col_size, sizeof(void*));

    size_t total_mem = cxt_mem + tables_mem + col_mem;
    // Validate that we don't have an overflow in our size calculations.
    assert(safe_add_size(cxt_mem, tables_mem, &total_mem) && safe_add_size(total_mem, col_mem, &total_mem));

    uint8_t* mem = (uint8_t*)malloc(total_mem);
    if (mem == NULL)
        return NULL;

    // Copy passed in state
    mdcxt_t* pcxt = (mdcxt_t*)mem;
    mem += cxt_mem;
    memcpy(pcxt, cxt, sizeof(*cxt));
    assert(pcxt->tables == NULL);

    // Zero out the remaining memory
    memset(mem, 0, total_mem - cxt_mem);

    // Update the tables pointer to offset in allocation
    pcxt->tables = (mdtable_t*)mem;
    mem += tables_mem;

    // Update each table's column array
    for (mdtable_id_t id = mdtid_First; id < mdtid_End; ++id)
    {
        pcxt->tables[id].column_details = (mdtcol_t*)mem;
        uint32_t size = table_col_sizes[id];
        mem += size;
    }

    assert(mem <= (uint8_t*)pcxt + total_mem);
    return pcxt;
}

bool md_create_handle(void const* data, size_t data_len, mdhandle_t* handle)
{
    if (data == NULL || handle == NULL)
        return false;

    uint8_t const* const base = data;
    uint8_t const* curr = data;
    size_t curr_len = data_len;

    // Validate that the metadata root is minimally valid before creating a handle.
    uint32_t sig;
    uint32_t ver_buf_count;
    uint16_t stream_count;

    mdcxt_t cxt;
    memset(&cxt, 0, sizeof(cxt));

    // Consume header defined in II.24.2.1
    if (!read_u32(&curr, &curr_len, &sig) || sig != METADATA_SIG)
        return false;

    if (!read_u16(&curr, &curr_len, &cxt.major_ver)
        || !read_u16(&curr, &curr_len, &cxt.minor_ver)
        || !advance_stream(&curr, &curr_len, 4)
        || !read_u32(&curr, &curr_len, &ver_buf_count))
    {
        return false;
    }

    // The version count is aligned to 4-bytes
    ver_buf_count = align_to(ver_buf_count, 4);
    if (ver_buf_count > curr_len)
        return false;

    // Confirm terminator and consume the version/aligned length
    cxt.version = (char const*)curr;
    if (ver_buf_count == 0
        || cxt.version[ver_buf_count - 1] != '\0'
        || !advance_stream(&curr, &curr_len, ver_buf_count))
    {
        return false;
    }

    if (!read_u16(&curr, &curr_len, &cxt.flags)
        || !read_u16(&curr, &curr_len, &stream_count))
    {
        return false;
    }

    // Iterate over the discovered streams
    uint32_t offset;
    uint32_t stream_size;
    uint8_t* name_end;
    size_t name_len;
    bool tables_heap_uncompressed = false;
    for (size_t i = 0; i < stream_count; ++i)
    {
        if (!read_u32(&curr, &curr_len, &offset)
            || !read_u32(&curr, &curr_len, &stream_size))
        {
            return false;
        }

        // Verify the offset is valid for our data size
        if (offset > data_len)
            return false;

        // Verify the stream size fits within the available data
        if (stream_size > data_len - offset)
            return false;

        // Find the terminating null.
        name_end = memchr(curr, 0, curr_len);
        if (name_end == NULL)
            return false;

        name_len = name_end - curr;
        if (strncmp((char const*)curr, "#~", name_len) == 0)
        {
            cxt.tables_heap.ptr = base + offset;
            cxt.tables_heap.size = stream_size;
            tables_heap_uncompressed = false;
        }
        // The #- stream is used for images that may have the *Ptr indirection tables.
        // The indirection tables, as well as the #- stream, are not documented in the ECMA spec.
        else if (strncmp((char const*)curr, "#-", name_len) == 0)
        {
            cxt.tables_heap.ptr = base + offset;
            cxt.tables_heap.size = stream_size;
            tables_heap_uncompressed = true;
        }
        // The #JTD stream is a marker that the image is a minimal EnC delta, as compared to an image
        // with the EnC data included. This stream is not documented in the ECMA spec.
        else if (strncmp((char const*)curr, "#JTD", name_len) == 0)
        {
            // The content of the stream is ignored.
            cxt.context_flags |= mdc_minimal_delta;
        }
        else if (strncmp((char const*)curr, "#Strings", name_len) == 0)
        {
            cxt.strings_heap.ptr = base + offset;
            cxt.strings_heap.size = stream_size;

            // Compute the precise size of the string heap by walking back over the trailing null padding.
            // There may be up to three extra '\0' characters appended for padding.
            // ENC minimal delta images require the precise size of the base image string heap to be known,
            // so we trim the trailing padding.
            uint8_t const* p = cxt.strings_heap.ptr + cxt.strings_heap.size - 1;
            while (cxt.strings_heap.size >= 2 && p[0] == 0 && p[-1] == 0)
            {
                p--;
                cxt.strings_heap.size--;
            }
        }
        else if (strncmp((char const*)curr, "#Blob", name_len) == 0)
        {
            cxt.blob_heap.ptr = base + offset;
            cxt.blob_heap.size = stream_size;
        }
        else if (strncmp((char const*)curr, "#US", name_len) == 0)
        {
            cxt.user_string_heap.ptr = base + offset;
            cxt.user_string_heap.size = stream_size;
        }
        else if (strncmp((char const*)curr, "#GUID", name_len) == 0)
        {
            cxt.guid_heap.ptr = base + offset;
            cxt.guid_heap.size = stream_size;
        }
#ifdef DNMD_PORTABLE_PDB
        else if (strncmp((char const*)curr, "#Pdb", name_len) == 0)
        {
            cxt.pdb.ptr = base + offset;
            cxt.pdb.size = stream_size;
        }
#endif // DNMD_PORTABLE_PDB
        else
        {
            assert(!"Unknown stream");
            return false;
        }

        // Align the string length to 4 bytes.
        if (!advance_stream(&curr, &curr_len, align_to((uint32_t)(name_len + 1), 4)))
            return false;
    }

    // When the #JTD stream is present, the #- stream must be
    // the stream that contains the metadata tables.
    if ((bool)(cxt.context_flags & mdc_minimal_delta) && !tables_heap_uncompressed)
        return false;

    // Record whether the table heap is uncompressed (#-).
    if (tables_heap_uncompressed)
        cxt.context_flags |= mdc_uncompressed_table_heap;

    // Header initialization is complete.
    cxt.magic = MDLIB_MAGIC_NUMBER;
    cxt.raw_metadata.ptr = data;
    cxt.raw_metadata.size = data_len;

    // Allocate and initialize a context
    mdcxt_t* pcxt = allocate_full_context(&cxt);
    if (pcxt == NULL)
        return false;

#ifndef NDEBUG
    memset(&cxt, 0xcc, sizeof(cxt));
#endif // NDEBUG

    // Initialize the tables in the new context.
    if (!initialize_tables(pcxt))
    {
        free(pcxt);
        return false;
    }

    // Move the constructed context to the allocated one.
    *handle = pcxt;
    return true;
}

void md_destroy_handle(mdhandle_t handle)
{
    mdcxt_t* cxt = extract_mdcxt(handle);
    if (cxt == NULL)
        return;

    free(cxt);
}

#ifdef DNMD_PORTABLE_PDB
bool md_get_pdb_id(mdhandle_t handle, size_t* pdb_id_len, uint8_t* pdb_id)
{
    mdcxt_t* cxt = extract_mdcxt(handle);
    if (cxt == NULL)
        return false;

    md_pdb_t pdb;
    if (!try_get_pdb(cxt, &pdb))
        return false;

    if (!pdb_id_len)
        return false;

    size_t input_len = *pdb_id_len;
    *pdb_id_len = ARRAY_SIZE(pdb.pdb_id);

    if (input_len < ARRAY_SIZE(pdb.pdb_id))
        return false;

    memcpy(pdb_id, pdb.pdb_id, ARRAY_SIZE(pdb.pdb_id));
    return true;
}
#endif

mdcxt_t* extract_mdcxt(mdhandle_t md)
{
    mdcxt_t* cxt = (mdcxt_t*)md;
    if (!cxt || cxt->magic != MDLIB_MAGIC_NUMBER)
        return NULL;
    return cxt;
}
