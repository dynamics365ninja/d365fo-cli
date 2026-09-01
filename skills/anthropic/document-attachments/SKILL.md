---
name: document-attachments
description: Attach files to a record in D365FO — the DocuRef/DocuValue/DocuType tables, DocumentManagement::attachFile and its nine arguments, and reading an attachment back. Invoke when the user asks to attach a document, store a file against a record, read attachments, or debug an attachment that saved with the wrong name.
applies_when: User intent involves attaching or reading a file on a record, document handling, DocuRef, attachment types, or an attachment whose name or notes came out wrong.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Document attachments

## The three tables, and which is which

An attachment is not one row. It is a row in each of two tables plus a
configuration row:

| Object | Kind | Holds |
|---|---|---|
| `DocuRef` | **table** | the link: which record the attachment belongs to, and its type |
| `DocuValue` | **table** | the file itself — content, name, extension |
| `DocuType` | **table** | the attachment TYPE: where files of this type are stored and how they behave |

`DocumentManagement` and `DocuAction` are **classes**; the three above are
**tables**. Reaching for `DocumentManagement` when you want the link row, or
treating `DocuRef` as a class, is the usual first wrong turn — check with
`d365fo get table DocuRef` and `d365fo get class DocumentManagement`.

A `DocuRef` points at its record by the trio `RefTableId` / `RefRecId` /
`RefCompanyId`. That is why attaching needs the table id and not the buffer:
the link is by identity, not by reference.

## Attaching a file

`DocumentManagement::attachFile` takes **nine** arguments, the last optional:

```
public static DocuRef attachFile(
    TableId           _refTableId,
    RefRecId          _refRecId,
    DataAreaId        _refDataAreaId,
    DocuTypeId        _type,
    System.IO.Stream  _file,
    str               _fileName,
    str               _fileContentType,
    str               _attachmentName,
    str               _notes = '')
```

**The eight-argument call compiles.** `_notes` is optional, so passing eight
arguments is legal X++ — and if the eighth value you meant as *notes* lands in
`_attachmentName`, the attachment is stored under the note text and the notes
are empty. Nothing fails; the record is simply wrong, and it is wrong in a way
that only shows when someone opens the attachment list.

This is the general lesson for any call with optional tail parameters: **"it
compiles" is not "it is correct"**. The compiler cannot answer a question about
argument *meaning*. Read the signature and count.

```xpp
DocuRef docuRef = DocumentManagement::attachFile(
    tableNum(CustTable),
    custTable.RecId,
    custTable.DataAreaId,
    'File',                       // a DocuTypeId that exists in DocuType
    stream,
    'contract.pdf',
    'application/pdf',
    'Signed contract',            // _attachmentName
    'Countersigned 2026-03-01');  // _notes
```

`_type` is a `DocuTypeId` — the id of a row in `DocuType`, not a free string.
An id no `DocuType` row carries fails at run time, not at compile time.

## Reading attachments back

Select `DocuRef` by the same trio, and join `DocuValue` for the file:

```xpp
DocuRef   docuRef;
DocuValue docuValue;

while select docuRef
    where docuRef.RefTableId   == tableNum(CustTable)
       && docuRef.RefRecId     == custTable.RecId
       && docuRef.RefCompanyId == custTable.DataAreaId
{
    docuValue = docuRef.docuValue();
    info(strFmt('%1 (%2)', docuValue.FileName, docuValue.FileType));
}
```

Do not assume every `DocuRef` has a file. A note-only attachment has a
`DocuRef` and no `DocuValue`, so `docuValue()` returns an empty buffer —
test `RecId` before using it.

## Checks

- `d365fo get table DocuRef` before writing the select: the field names are
  `RefTableId` / `RefRecId` / `RefCompanyId`, and guessing `RefDataAreaId`
  from the `attachFile` parameter name is a compile error.
- `d365fo validate xpp` on the class that attaches — the reference gate proves
  `DocumentManagement` and the `Docu*` tables exist rather than accepting them
  because they look right.
