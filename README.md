# transkribus-process

Pulls images from Islandora, uploads them to Transkribus for processing, gets the results and converts them to HOCR, and uploads the HOCR datastreams back into Islandora.

## Prerequisites

Expects drush (with islandora_datastreams_crud) and convert (provided by imagemagick) to be available on the system path.