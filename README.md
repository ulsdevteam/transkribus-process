# transkribus-process

Pulls images from Islandora, uploads them to Transkribus for processing, gets the results and converts them to HOCR, and uploads the HOCR datastreams back into Islandora.

## Prerequisites

Expects drush (with islandora_datastreams_crud), convert (provided by imagemagick), and xslt3 (`npm install -g xslt3`) to be available on the system path.

## alto_to_hocr.sef.json

This is a compiled xslt produced by running xslt3 on https://github.com/filak/hOCR-to-ALTO/blob/master/alto__hocr.xsl

`xslt3 -xsl:../hOCR-to-ALTO/alto__hocr.xsl -export:alto_to_hocr.sef.json -relocate:on -nogo`

## Building

Building a Linux executable on Windows:
`dotnet publish --os linux -p:PublishSingleFile=true --self-contained true`