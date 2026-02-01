cmake_minimum_required(VERSION 3.16)
project(OmenUI VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

# Find Qt6 (fallback to Qt5)
find_package(Qt6 COMPONENTS Widgets QUIET)
if(NOT Qt6_FOUND)
    find_package(Qt5 5.15 REQUIRED COMPONENTS Widgets)
    set(QT_VERSION_MAJOR 5)
else()
    set(QT_VERSION_MAJOR 6)
endif()

# Source files
set(SOURCES
    src/main.cpp
    src/mainwindow.cpp
    src/buildoutput.cpp
    src/projecttree.cpp
)

set(HEADERS
    include/mainwindow.h
    include/buildoutput.h
    include/projecttree.h
)

# Create executable
if(WIN32)
    add_executable(${PROJECT_NAME} WIN32 ${SOURCES} ${HEADERS})
else()
    add_executable(${PROJECT_NAME} ${SOURCES} ${HEADERS})
endif()

target_include_directories(${PROJECT_NAME} PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

if(Qt6_FOUND)
    target_link_libraries(${PROJECT_NAME} PRIVATE Qt6::Widgets)
else()
    target_link_libraries(${PROJECT_NAME} PRIVATE Qt5::Widgets)
endif()

# Install
install(TARGETS ${PROJECT_NAME} RUNTIME DESTINATION bin)
