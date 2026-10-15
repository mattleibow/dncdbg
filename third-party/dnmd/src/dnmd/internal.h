#ifndef _SRC_DNMD_INTERNAL_H_
#define _SRC_DNMD_INTERNAL_H_

#include <stdint.h>
#include <stdlib.h>
#include <stdbool.h>
#include <assert.h>
#include <string.h>
#include <corhdr.h>
#include <dnmd.h>
#ifdef DNMD_PORTABLE_PDB
#include <dnmd_pdb.h>
#endif

#define ARRAY_SIZE(a) (sizeof(a) / sizeof(*a))

// Safe arithmetic to prevent size_t overflow in allocations.
static inline bool safe_add_size(size_t a, size_t b, size_t* result)
{
    if (a > SIZE_MAX - b)
        return false;
    *result = a + b;
    return true;
}

static inline bool safe_mul_size(size_t a, size_t b, size_t* result)
{
    if (a != 0 && b > SIZE_MAX / a)
        return false;
    *result = a * b;
    return true;
}

#ifndef NDEBUG
#define ASSERT_ASSUME(x) assert(x)
#elif defined(_MSC_VER)
#define ASSERT_ASSUME(x) __assume(x)
#elif defined(__clang__)
#define ASSERT_ASSUME(x) __builtin_assume(x)
#elif defined(__GNUC__)
#define ASSERT_ASSUME(x) do { if (!(x)) __builtin_unreachable(); } while (0)
#else
#define ASSERT_ASSUME(x) (void)(x)
#endif

// Const data
typedef struct mdcdata__
{
    uint8_t const* ptr;
    size_t size;
} mdcdata_t;

// II.24.2.6 - 64 is the maximum value
#define MDTABLE_MAX_COUNT ((size_t)mdtid_End)
static_assert(MDTABLE_MAX_COUNT <= 64, "Specification sets max table count to 64");

// Macros for computing token types.
#define CreateTokenType(tk) (mdToken)(((uint32_t)tk << 24) & 0xff000000)
#define ExtractTokenType(tk) ((tk >> 24) & 0xff)

// Flags and masks used to embed column details for
// interpreting table rows.
typedef enum
{
    mdtc_none       = 0x00000000,

    // If the value should be taken as-is or used to index more
    mdtc_constant   = 0x00000001,
    mdtc_idx_heap   = 0x00000002, // Index into a heap - see flags below.
    mdtc_idx_table  = 0x00000004, // Index into a table - see mask below.
    mdtc_idx_coded  = 0x00000008, // Coded index - see II.24.2.6.
    // Value category mask
    mdtc_categorymask = 0x0000000f,

    // Size of the constant or index
    mdtc_b2         = 0x00000010, // 2-bytes
    mdtc_b4         = 0x00000020, // 4-bytes
    // Width of column flags
    mdtc_widthmask  = 0x00000030,

    //mdtc_unused1  = 0x00000040,
    //mdtc_unused2  = 0x00000080,

    // Column byte offset into row
    mdtc_comask     = 0x0000ff00, // Mask for storing column offset

    // Table values
    mdtc_timask     = 0x00ff0000, // Mask for storing table index

    // Coded index
    mdtc_cimask     = 0x0f000000, // Mask for storing coded index map index

    // Heap flags
    mdtc_hguid      = 0x10000000, // #GUID
    mdtc_hstring    = 0x20000000, // #Strings
    mdtc_hus        = 0x40000000, // #US
    mdtc_hblob      = 0x80000000, // #Blob
    mdtc_hmask      = 0xf0000000, // Mask for storing heap type
} mdtcol_t;

// Flags and masks for context details
typedef enum
{
    mdc_none                    = 0x0000,
    mdc_large_string_heap       = 0x0001,
    mdc_large_guid_heap         = 0x0002,
    mdc_large_blob_heap         = 0x0004,
    mdc_extra_data              = 0x0040,
    mdc_image_flags             = 0xffff,
    mdc_minimal_delta           = 0x00010000,
    mdc_uncompressed_table_heap = 0x00020000,
} mdcxt_flag_t;

// Macros used to insert/extract the column offset.
#define InsertOffset(o) ((o << 8) & mdtc_comask)
#define ExtractOffset(o) ((o & mdtc_comask) >> 8)

// Macros used to insert/extract the table for indexing.
#define InsertTable(c) ((c << 16) & mdtc_timask)
#define ExtractTable(c) ((c & mdtc_timask) >> 16)

// Macros used to insert/extract the coded index map index
#define InsertCodedIndex(s) ((s << 24) & mdtc_cimask)
#define ExtractCodedIndex(s) ((s & mdtc_cimask) >> 24)

// Forward declare.
struct mdcxt__;

typedef struct mdtable__
{
    mdcdata_t data;
    uint32_t row_count;
    uint8_t row_size_bytes;
    uint8_t column_count;
    bool is_sorted : 1;
    uint8_t table_id;
    struct mdcxt__* cxt; // Non-null is indication of complete initialization
    mdtcol_t* column_details;
} mdtable_t;

typedef mdcdata_t mdstream_t;

typedef struct mdcxt__
{
    uint32_t magic; // mdlib magic
    mdcdata_t raw_metadata; // metadata raw bytes
    mdcxt_flag_t context_flags;

    // Metadata root details - II.24.2.1
    uint16_t major_ver;
    uint16_t minor_ver;
    uint16_t flags;
    char const* version;

    // Metadata heaps - II.24.2.2
    mdstream_t strings_heap;
    mdstream_t guid_heap;
    mdstream_t blob_heap;
    mdstream_t user_string_heap;
    mdstream_t tables_heap;
#ifdef DNMD_PORTABLE_PDB
    mdstream_t pdb;
#endif // DNMD_PORTABLE_PDB

    // Metadata tables - II.22
    mdtable_t* tables;

} mdcxt_t;

// Extract a context from the mdhandle_t.
mdcxt_t* extract_mdcxt(mdhandle_t md);

//
// Streams
//

// Strings heap, #Strings - II.24.2.3
bool try_get_string(mdcxt_t* cxt, size_t offset, char const** str);

// Blob heap, #Blob - II.24.2.4
bool try_get_blob(mdcxt_t* cxt, size_t offset, uint8_t const** blob, uint32_t* blob_len);

// GUID heap, #GUID - II.24.2.5
bool try_get_guid(mdcxt_t* cxt, size_t idx, mdguid_t* guid);

// Table heap, #~ - II.24.2.6
// Note: This can only be done after all streams have been read in.
bool initialize_tables(mdcxt_t* cxt);

// PDB heap, #Pdb - https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md#pdb-stream
typedef struct md_pdb__
{
    uint8_t pdb_id[20];
    mdToken entry_point;
    uint64_t referenced_type_system_tables;
    uint32_t type_system_table_rows[MDTABLE_MAX_COUNT];
} md_pdb_t;

// Check if the context has a non-empty PDB stream.
bool has_pdb(mdcxt_t* cxt);

// Interpret in the PDB data stream
// The md_pdb_t will be fully initialized if "true" is returned.
bool try_get_pdb(mdcxt_t* cxt, md_pdb_t* pdb);

//
// Tables
//

// Coded index collections - II.24.2.6
typedef enum
{
    mdci_TypeDefOrRef,
    mdci_HasConstant,
    mdci_HasCustomAttribute,
    mdci_HasFieldMarshall,
    mdci_HasDeclSecurity,
    mdci_MemberRefParent,
    mdci_HasSemantics,
    mdci_MethodDefOrRef,
    mdci_MemberForwarded,
    mdci_Implementation,
    mdci_CustomAttributeType,
    mdci_ResolutionScope,
    mdci_TypeOrMethodDef,
#ifdef DNMD_PORTABLE_PDB
    mdci_HasCustomDebugInformation,
#endif // DNMD_PORTABLE_PDB
    mdci_Count
} md_coded_idx_t;

// Manipulators for coded indices - II.24.2.6
bool decompose_coded_index(uint32_t cidx, mdtcol_t col_details, mdtable_id_t* table_id, uint32_t* table_row);

// Get the column count for a table.
uint8_t get_table_column_count(mdtable_id_t id);

// Initialize the supplied table details
bool initialize_table_details(
    uint32_t const* all_table_row_counts,
    mdcxt_flag_t context_flags,
    mdtable_id_t id,
    bool is_sorted,
    mdtable_t* table);

// Given the current table, consume the data stream assuming it contains the rows
bool consume_table_rows(mdtable_t* table, uint8_t const** data, size_t* data_len);

// Cursor manipulation

// Internal function used to create a cursor.
// Limited validation is done for the arguments.
mdcursor_t create_cursor(mdtable_t* table, uint32_t row);

// We declare these functions as static so they can be included in each translation unit.
// Some units may not use them, so we ignore the unused function warning.
#ifdef __GNUC__
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wunused-function"
#endif
static mdtable_t* CursorTable(mdcursor_t* c)
{
    assert(c != NULL);
    return (mdtable_t*)c->_reserved1;
}

static uint32_t CursorRow(mdcursor_t* c)
{
    assert(c != NULL);
    return RidFromToken(c->_reserved2);
}

static bool CursorNull(mdcursor_t* c)
{
    return CursorRow(c) == 0;
}

static bool CursorEnd(mdcursor_t* c)
{
    return (CursorTable(c)->row_count + 1) == CursorRow(c);
}

static uint8_t col_to_index(col_index_t col_idx, mdtable_t const* table)
{
    assert(table != NULL);
    uint32_t idx = (uint32_t)col_idx;
#ifdef DEBUG_TABLE_COLUMN_LOOKUP
    mdtable_id_t tgt_table_id = col_idx >> 8;
    if (tgt_table_id != table->table_id)
    {
        assert(!"Unexpected table/column indexing");
        return false;
    }
    idx = (col_idx & 0xff);
#else
    (void)table;
#endif
    return (uint8_t)idx;
}

#ifdef __GNUC__
#pragma GCC diagnostic pop
#endif


// Single column access
typedef struct access_cxt__
{
    mdtable_t* table;
    mdtcol_t col_details;
    uint8_t const* data;
} access_cxt_t;

bool create_access_context(mdcursor_t* cursor, col_index_t col_idx, access_cxt_t* acxt);
bool read_column_data(access_cxt_t* acxt, uint32_t* data);

//
// Manipulation of bits
//

uint32_t align_to(uint32_t val, uint32_t align);
size_t count_set_bits(uint64_t val);

//
// Byte streams
//

bool advance_stream(uint8_t const** data, size_t* data_len, size_t b);

bool read_u8(uint8_t const** data, size_t* data_len, uint8_t* o);
bool read_u16(uint8_t const** data, size_t* data_len, uint16_t* o);
bool read_u32(uint8_t const** data, size_t* data_len, uint32_t* o);
bool read_u64(uint8_t const** data, size_t* data_len, uint64_t* o);

bool advance_output_stream(uint8_t** data, size_t* data_len, size_t b);

bool write_u8(uint8_t** data, size_t* data_len, uint8_t o);

// II.23.2
bool decompress_u32(uint8_t const** data, size_t* data_len, uint32_t* o);
bool decompress_i32(uint8_t const** data, size_t* data_len, int32_t* o);

#endif // _SRC_DNMD_INTERNAL_H_
