# Attachments

## What customers send

Files attached to a mail sent to the support address are kept with the mail and shown in the
ticket's **Attachments** card when the mail is filed on the ticket. Click a file to download it.
Pictures pasted into the mail itself, such as screenshots, are kept the same way, named after
their file name or *inline-image-1.png*.
Files from a duplicate merged into a ticket are shown there too.

A file that couldn't be kept is still listed, in red, with the reason:

- **No attachment storage is configured.** Ask whoever administers the tenant to choose one under
  **Support › Mail › Attachments**.
- **Larger than 25 MB**, or over 50 MB together with the mail's other files.
- **The upload failed.** The storage couldn't be reached when the mail arrived.

The customer's mail text is on the ticket either way. Only the file is missing, and the reason
tells you whether to ask them to send it another way.

## Adding your own

On an open ticket, choose files in the **Attachments** card. Tick **The customer sees what I
attach** if it's for them. A note *Attached: …* goes on the history, visible to the customer
when the files are. If attachments can't be kept, the card says why and no file chooser is shown.

## What the customer sees

In the customer portal, a ticket lists its **Files**: what the customer sent, and what you added
with **The customer sees what I attach** ticked. The customer downloads them through the portal.

- Files you added without ticking it are never listed, and can't be fetched by guessing an address.
- A file that couldn't be kept is listed as *could not be kept; please send it again*, without the
  technical reason.
- A bounced mail's files are never shown. The bounce is an internal note, and what it carries is
  our own returned mail.
- A duplicate merged into the ticket brings its files along only if it was the same customer's.
  A duplicate merged in from another customer shows its files to agents only.

## Safety

Files are always downloaded, never opened inside EntKube, and a type a browser could run, such as
a web page or an SVG image, is downloaded as a plain file. Files are fetched through EntKube, which
checks that you have access to the tenant, or, from the portal, to that customer and that the
file is on one of their tickets. Nobody gets a direct link to the storage.
