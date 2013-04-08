O5MParser
=========

O5MParser is a C# library to parse a subset of the OpenStreetMap O5M file format. It was made for my own small uses and is thus incomplete.

It supports reading the following data:
* Map Boundary
* Way (header, node references and list of tags)
* Node (lat/lon, header and list of tags)

Ways and Nodes are the main features required for building your own maps, and the program can be extended easily. It was created quickly and lacks documentation.

It is freely released under the MIT License, see the COPYING file for details.
