# transkribus-process

Pulls images from Islandora, uploads them to Transkribus for processing, gets the results and converts them to HOCR, and pushes the HOCR datastreams back into Islandora.

## Process flow

transkribus-process is a command-line-interface application that orchestrates uploading image datastreams to Transkribus, processing the results into a format that Islandora accepts, and pushing OCR and hOCR datastreams back to Islandora. It has two modes of operation, called 'upload' and 'check'. Standard usage is to upload and then check, but it is also possible to run each step individually, which could be convenient for processing large batches.

In upload mode, it takes as input the Islandora PID of a manuscript and an HTR ID, which identifies the Transkribus model to be used. Using drush commands provided by islandora_datastreams_crud, it gets the id of all pages in the manuscript, and then fetches the jp2 image datastreams for each of those pages into a temporary folder. Transkribus won't accept jp2 files, so it uses the imagemagick convert tool to convert them into jpegs before uploading them via the Transkribus API. After uploading each image, the Transkribus API returns a Process ID that is used to retrieve the finished text. A record containing the page PID and the Process ID is saved into a mysql database indicating that page is currently in progress; it also notes the HTR ID, the current time, and the active user. Once all the pages have been uploaded, it waits a few seconds before switching to check mode.

In check mode, it looks at its database to see what pages are still marked as in progress, and queries the Transkribus API to see if they are done processing. If they are, the results are downloaded to a temporary folder in ALTO XML format. Using an XSLT transformer, the ALTO XML is converted to the hOCR format expected by Islandora. OCR plaintext is generated from that hOCR, and then both of those datastreams are pushed to the page with islandora_datastreams_crud. This process is repeated until no more pages are still in progress.

## Prerequisites

Requires Islandora 7.x-1.x.  Expects drush (with islandora_datastreams_crud), convert (provided by imagemagick), and xslt3 (`npm install -g xslt3`) to be available on the system path.

## Usage

    transkribus-process --uri https://uri-of-islandora-instance --root /path/to/drupal/root --user $USER --htrid 12345 --pid islandora_pid

Rather than providing a pid, you can also use `--pidfile` to provide a path to a pidfile created using islandora_datastreams_crud fetch_pids.

There are also `upload` and `check` commands that just send images to Transkribus and just check for completed transcriptions respectively.

`htrid`s for publically available models can be found here: https://readcoop.eu/transkribus/public-models/

## alto_to_hocr.sef.json

This is a compiled xslt produced by running xslt3 on https://github.com/filak/hOCR-to-ALTO/blob/master/alto__hocr.xsl

`xslt3 -xsl:../hOCR-to-ALTO/alto__hocr.xsl -export:alto_to_hocr.sef.json -relocate:on -nogo`

## Building

Building a Linux executable on Windows:
`dotnet publish --os linux -p:PublishSingleFile=true --self-contained true`
